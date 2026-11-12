using System.Data;
using Bloom.Launch.Data;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Domain;

public static class Views
{
    /// <summary>The admin view without products and SKUs (the list).</summary>
    public static AdminLaunch Admin(LaunchRow row) => new()
    {
        Id = row.Id.ToString("D"),
        Name = row.Name,
        Stage = row.Stage,
        LaunchTime = Timestamp.FromDateTimeOffset(row.LaunchTime),
        ReleaseDate = Timestamp.FromDateTimeOffset(row.ReleaseDate),
        AdmissionRate = row.AdmissionRate,
        WaitingRoomClosedAt = row.WaitingRoomClosedAt is { } closed ? Timestamp.FromDateTimeOffset(closed) : null,
        Version = row.Version,
        CreatedAt = Timestamp.FromDateTimeOffset(row.CreatedAt),
        UpdatedAt = Timestamp.FromDateTimeOffset(row.UpdatedAt),
    };

    /// <summary>The full admin view, read from the database now (products by <c>product_id</c>, SKUs by <c>sku_id</c>); null when there is no such launch.
    /// The three reads share one read-only snapshot, so a concurrent hold or edit cannot give a mixed view. The context must have no transaction open.</summary>
    public static async Task<AdminLaunch?> AdminAsync(LaunchDbContext db, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlAsync($"SET TRANSACTION READ ONLY", ct);
        if (await db.Launches.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct) is not { } row) return null;
        var view = Admin(row);
        view.Products.AddRange(await db.LaunchProducts.AsNoTracking().Where(p => p.LaunchId == id).OrderBy(p => p.ProductId)
            .Select(p => new LaunchProduct { ProductId = p.ProductId.ToString("D"), PurchaseLimit = p.PurchaseLimit }).ToListAsync(ct));
        view.Skus.AddRange(await db.LaunchSkus.AsNoTracking().Where(s => s.LaunchId == id).OrderBy(s => s.SkuId)
            .Select(s => new AdminSku { SkuId = s.SkuId.ToString("D"), ProductId = s.ProductId.ToString("D"), Allocation = s.Allocation, Held = s.Held, Sold = s.Sold }).ToListAsync(ct));
        return view;
    }
}
