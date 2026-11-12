using Bloom.Catalog.Data;
using Bloom.Catalog.Storage;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Worker;

/// <summary>Daily: deletes <c>pending</c> images older than 24 h and their objects (08-catalog §6.7). Pending images are never public, so no cache to drop.</summary>
public sealed class ImageCleanupJob(IServiceScopeFactory scopes, ImageStore store, TimeProvider time, ILogger<ImageCleanupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Image cleanup failed; retrying in 24 hours");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Object first, then row; an object that can't be deleted keeps its row for the next run. Returns the rows deleted.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddHours(-24);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var stale = await db.ProductImages.AsNoTracking().Where(i => i.Status == ImageStatuses.Pending && i.CreatedAt < cutoff)
            .Select(i => new { i.Id, i.StorageKey }).ToListAsync(ct);
        var deleted = 0;
        foreach (var image in stale)
        {
            try
            {
                await store.DeleteAsync(image.StorageKey);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not delete object {Key} of pending image {ImageId}; retrying next run", image.StorageKey, image.Id);
                continue;
            }
            deleted += await db.ProductImages.Where(i => i.Id == image.Id && i.Status == ImageStatuses.Pending).ExecuteDeleteAsync(ct);
        }
        return deleted;
    }
}
