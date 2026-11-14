using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Shared.Tests;

public class NowTests(Containers containers)
{
    [Fact]
    public async Task Now_is_the_same_value_for_the_whole_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(await containers.CreateDatabaseAsync()).Options;
        await using var db = new TestDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var first = await db.Database.NowAsync(ct);
        await Task.Delay(50, ct);
        var second = await db.Database.NowAsync(ct);

        Assert.Equal(first, second);
    }
}
