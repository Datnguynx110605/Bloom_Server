using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bloom.Shared.Tests;

public class AfterCommitTests(Containers containers)
{
    private sealed class AfterCommitDbContext(DbContextOptions<AfterCommitDbContext> options) : DbContext(options), IAfterCommitContext
    {
        public AfterCommitActions AfterCommit { get; } = new();
    }

    private sealed class CapturingLogger : ILogger<AfterCommitInterceptor>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private readonly CapturingLogger _log = new();

    private async Task<AfterCommitDbContext> NewContextAsync()
    {
        var options = new DbContextOptionsBuilder<AfterCommitDbContext>()
            .UseNpgsql(await containers.CreateDatabaseAsync())
            .AddInterceptors(new AfterCommitInterceptor(_log))
            .Options;
        return new AfterCommitDbContext(options);
    }

    [Fact]
    public async Task Runs_after_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewContextAsync();
        var ran = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AfterCommit.Add(() => { ran++; return Task.CompletedTask; });
        Assert.Equal(0, ran);
        await tx.CommitAsync(ct);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Dropped_after_rollback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewContextAsync();
        var ran = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AfterCommit.Add(() => { ran++; return Task.CompletedTask; });
        await tx.RollbackAsync(ct);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Later_transaction_does_not_run_rolled_back_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewContextAsync();
        var ran = new List<string>();
        await using (await db.Database.BeginTransactionAsync(ct))
            db.AfterCommit.Add(() => { ran.Add("X"); return Task.CompletedTask; }); // disposed without commit
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AfterCommit.Add(() => { ran.Add("Y"); return Task.CompletedTask; });
        await tx.CommitAsync(ct);
        Assert.Equal(["Y"], ran);
    }

    [Fact]
    public async Task Throwing_action_is_logged_and_the_next_still_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewContextAsync();
        var ran = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AfterCommit.Add(() => throw new InvalidOperationException("boom"));
        db.AfterCommit.Add(() => { ran++; return Task.CompletedTask; });
        await tx.CommitAsync(ct);
        Assert.Equal(1, ran);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Error);
    }
}
