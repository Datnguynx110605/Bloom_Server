using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Stripe;
using Bloom.Payment.Worker;
using Bloom.Shared.Persistence;
using System.Data.Common;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Payment.Tests;

public sealed class TestPaymentOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Uses this Stripe double instead of a new one, so two hosts on one database see the same Stripe (an API host and a worker host).</summary>
    public FakeStripe? Stripe { get; set; }

    /// <summary>Config keys (for example <c>Payment:StorefrontBaseUrl</c>) that replace the defaults of <see cref="TestPayment.DefaultConfig"/>.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>Counts the EF transactions started and not yet committed or rolled back (the app uses the async API only).</summary>
public sealed class TransactionWatch : DbTransactionInterceptor
{
    private int _open;

    public int Open => Volatile.Read(ref _open);

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _open);
        return new(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        Interlocked.Decrement(ref _open);
        return Task.CompletedTask;
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        Interlocked.Decrement(ref _open);
        return Task.CompletedTask;
    }
}

/// <summary>The payment app built through <see cref="PaymentApp.Build"/> on <c>TestServer</c> (plan R1), on its own database (plan R16).</summary>
public sealed class TestPayment : IAsyncDisposable
{
    private readonly WebApplication _app;

    private readonly GrpcChannel _channel;

    private TestPayment(WebApplication app, GrpcChannel channel)
    {
        _app = app;
        _channel = channel;
        Internal = new(channel);
    }

    /// <summary>The internal gRPC client, called in-process (plan R1). Later tasks add the staff client here.</summary>
    public PaymentInternal.PaymentInternalClient Internal { get; }

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The EF transactions currently open in this host.</summary>
    public required TransactionWatch Transactions { get; init; }

    /// <summary>The Stripe double behind the app's real <see cref="StripeClient"/>; one per host (unless shared through <see cref="TestPaymentOptions.Stripe"/>), on the test clock.</summary>
    public required FakeStripe Stripe { get; init; }

    /// <summary>The connection string of the database (the app connects as its owner, plan R2).</summary>
    public required string ConnectionString { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestPayment> StartAsync(Containers containers, Action<TestPaymentOptions>? configure = null)
    {
        var options = new TestPaymentOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        // PostgreSQL keeps microseconds; Linux clocks have 100 ns ticks
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
        var logs = new LogCapture();
        var transactions = new TransactionWatch();
        var stripe = options.Stripe ?? new FakeStripe(clock);

        var config = DefaultConfig(connectionString, containers.RabbitMqUri);
        config["Payment:Mode"] = options.Mode;
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = PaymentApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
            b.Services.ConfigureDbContext<PaymentDbContext>(o => o.AddInterceptors(transactions));
            b.Services.AddHttpClient<IPaymentProvider, StripeClient>().ConfigurePrimaryHttpMessageHandler(() => stripe); // the app's own registration adds the rest
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // the app never migrates (plan R2)
        }
        await app.StartAsync();
        var server = app.GetTestServer();
        return new TestPayment(app, GrpcChannel.ForAddress(server.BaseAddress, new() { HttpHandler = server.CreateHandler() }))
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            Transactions = transactions,
            Stripe = stripe,
            ConnectionString = connectionString,
        };
    }

    /// <summary>Every key of plan R4 except <c>Mode</c>, with valid values.</summary>
    internal static Dictionary<string, string?> DefaultConfig(string database, string rabbitMq) => new()
    {
        ["Payment:Database"] = database,
        ["Payment:RabbitMq"] = rabbitMq,
        ["Payment:StorefrontBaseUrl"] = "https://shop.test",
        ["Payment:Stripe:ApiKey"] = "sk_test_x",
        ["Payment:Stripe:WebhookSecret"] = "whsec_test",
        ["Payment:Stripe:BaseUrl"] = "https://stripe.test/",
    };

    /// <summary>A test-side context on the same database.</summary>
    public PaymentDbContext NewDb() => NewDb(ConnectionString);

    private static PaymentDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(connectionString).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The payment row of an order.</summary>
    public async Task<PaymentRow> PaymentByOrderAsync(Guid orderId)
    {
        await using var db = NewDb();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId, Ct);
    }

    public async Task<PaymentRow> PaymentAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
    }

    public async Task<RefundRow> RefundAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
    }

    public async Task SqlAsync(FormattableString sql)
    {
        await using var db = NewDb();
        await db.Database.ExecuteSqlAsync(sql, Ct);
    }

    /// <summary>The database's <c>now()</c>: the clock stored timestamps and time checks use, which the fake clock does not drive.</summary>
    public async Task<DateTimeOffset> DbNowAsync()
    {
        await using var db = NewDb();
        return await db.Database.NowAsync(Ct);
    }

    /// <summary>Moves every stored time back by <paramref name="span"/>, as if that much time had passed since the rows were written: the database clock cannot be advanced.</summary>
    public async Task AgeAsync(TimeSpan span)
    {
        await SqlAsync($"""
            UPDATE payments SET created_at = created_at - {span}, updated_at = updated_at - {span}, session_expires_at = session_expires_at - {span},
                locked_until = locked_until - {span}, succeeded_at = succeeded_at - {span}
            """);
        await SqlAsync($"UPDATE refunds SET created_at = created_at - {span}, updated_at = updated_at - {span}, next_attempt_at = next_attempt_at - {span}");
    }

    /// <summary>Sessions of this database waiting for a lock, so a test can tell a second transaction is queued behind the first.</summary>
    public async Task<int> LockWaitersAsync()
    {
        await using var db = NewDb();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()").SingleAsync(Ct);
    }

    /// <summary>One pass of the provider executor.</summary>
    public Task RunExecutorAsync() => ActivatorUtilities.CreateInstance<ProviderExecutor>(Services).RunOnceAsync(Ct);

    public int ErrorLogs => Logs.Entries.Count(e => e.StartsWith("[Error]", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        _channel.Dispose();
        await _app.DisposeAsync();
    }
}
