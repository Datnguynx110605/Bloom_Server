using System.Diagnostics.Metrics;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Errors;
using Bloom.Shared.Observability;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Shared.Tests;

public class ConcurrencyTests(Containers containers)
{
    [Fact]
    public async Task T17_fifty_concurrent_conditional_updates_on_one_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new MetricCapture(host.Services.GetRequiredService<BloomOptions>().ServiceName);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Counters.Add(new Counter { Id = "sku", Held = 0, Allocation = 1 });
            await db.SaveChangesAsync(ct);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ConcurrentUpdates>().TryConditionalUpdateAsync(
                "sku-held", $"UPDATE counters SET held = held + 1 WHERE id = 'sku' AND held + 1 <= allocation", ct);
        })));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(49, results.Count(r => !r));
        using var check = host.Services.CreateScope();
        Assert.Equal(1, (await check.ServiceProvider.GetRequiredService<TestDbContext>().Counters.SingleAsync(ct)).Held);
        Assert.Equal(49, capture.Sum("conditional_update_rejections_total", "counter", "sku-held"));
    }

    [Fact]
    public async Task T18_stale_version_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new MetricCapture(host.Services.GetRequiredService<BloomOptions>().ServiceName);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var id = Guid.NewGuid();
        db.Items.Add(new Item { Id = id, Name = "a" });
        await db.SaveChangesAsync(ct);
        var updates = scope.ServiceProvider.GetRequiredService<ConcurrentUpdates>();

        var newVersion = await updates.UpdateVersionedAsync("item", db.Items.Where(i => i.Id == id), 0,
            s => s.SetProperty(i => i.Name, "b"), "ITEM_NOT_FOUND", ct);
        Assert.Equal(1, newVersion);

        var ex = await Assert.ThrowsAsync<RpcException>(() => updates.UpdateVersionedAsync("item",
            db.Items.Where(i => i.Id == id), 0, s => s.SetProperty(i => i.Name, "c"), "ITEM_NOT_FOUND", ct));
        Assert.Equal(StatusCode.Aborted, ex.StatusCode);
        Assert.Equal(BloomErrors.ConcurrencyConflict, ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason);

        var row = await db.Items.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        Assert.Equal("b", row.Name);
        Assert.Equal(1, row.Version);
        Assert.Equal(1, capture.Sum("optimistic_conflicts_total", "entity", "item"));
    }

    [Fact]
    public async Task Versioned_update_on_missing_row_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var missing = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            scope.ServiceProvider.GetRequiredService<ConcurrentUpdates>().UpdateVersionedAsync("item",
                db.Items.Where(i => i.Id == missing), 0, s => s.SetProperty(i => i.Name, "x"), "ITEM_NOT_FOUND", ct));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal("ITEM_NOT_FOUND", ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason);
    }
}
