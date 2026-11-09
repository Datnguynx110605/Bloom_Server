using Bloom.Launch.Contracts;
using Bloom.Launch.Passes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Launch.Tests;

/// <summary>Helpers for <c>HoldAllocation</c> tests (01-launch §6.5).</summary>
public static class Holds
{
    /// <summary>A pass token signed with the app's <see cref="PassTokens"/>. <c>exp</c> is the real UTC now plus <paramref name="ttl"/> (default 10 minutes): the database clock is the real one, not the fake app clock.</summary>
    public static string Token(TestLaunch t, Guid launch, Guid account, TimeSpan? ttl = null, Guid? passId = null) =>
        t.Services.GetRequiredService<PassTokens>().Sign(passId ?? Guid.CreateVersion7(), account, launch, DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds());

    public static HoldAllocationRequest Request(Guid orderId, string token, params (Guid Sku, int Qty)[] lines) =>
        new() { OrderId = orderId.ToString("D"), PassToken = token, Lines = { lines.Select(l => new HoldLine { SkuId = l.Sku.ToString("D"), Quantity = l.Qty }) } };

    public static Task<HoldAllocationResponse> CallAsync(TestLaunch t, Guid account, HoldAllocationRequest r) =>
        t.Internal.HoldAllocationAsync(r, Api.Customer(account), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    /// <summary>LR-01 and LR-02 hold, and the counters equal what the holds' lines say: <c>held + sold ≤ allocation</c> per SKU, <c>held + sold ≤ limit</c> per limit row,
    /// and <c>launch_skus.held/sold</c> are the sums over <c>hold_lines</c> of <c>held</c>/<c>sold</c> holds.</summary>
    public static async Task InvariantAsync(TestLaunch t, Guid launch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = t.NewDb();
        var skus = await db.LaunchSkus.AsNoTracking().Where(s => s.LaunchId == launch).ToListAsync(ct);
        var limits = await db.LaunchProducts.AsNoTracking().Where(p => p.LaunchId == launch).ToDictionaryAsync(p => p.ProductId, p => p.PurchaseLimit, ct);
        var usage = await db.LimitUsage.AsNoTracking().Where(u => u.LaunchId == launch).ToListAsync(ct);
        var lines = await (from l in db.HoldLines.AsNoTracking() join h in db.Holds.AsNoTracking() on l.HoldId equals h.Id where h.LaunchId == launch select new { l.SkuId, l.Quantity, h.Status }).ToListAsync(ct);

        foreach (var s in skus)
        {
            Assert.True((long)s.Held + s.Sold <= s.Allocation, $"SKU {s.SkuId}: held {s.Held} + sold {s.Sold} > allocation {s.Allocation}");
            Assert.Equal(lines.Where(l => l.SkuId == s.SkuId && l.Status == "held").Sum(l => (long)l.Quantity), s.Held);
            Assert.Equal(lines.Where(l => l.SkuId == s.SkuId && l.Status == "sold").Sum(l => (long)l.Quantity), s.Sold);
        }
        foreach (var u in usage)
            Assert.True((long)u.Held + u.Sold <= limits[u.ProductId], $"Account {u.AccountId}, product {u.ProductId}: held {u.Held} + sold {u.Sold} > limit {limits[u.ProductId]}");
    }
}
