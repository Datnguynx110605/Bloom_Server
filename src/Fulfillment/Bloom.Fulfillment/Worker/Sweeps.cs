using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Worker;

/// <summary>Hourly sweeps (05-fulfillment §6.3, spec §7). Later tasks add the return-expiry and daily label-check passes to the same loop.</summary>
public sealed class Sweeps(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Sweeps> logger) : BackgroundService
{
    private static readonly TimeSpan Silence = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await RunPossiblyLostAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Possibly-lost sweep failed; retrying in 1 h");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>
    /// Outbound <c>handed_over</c> / <c>in_transit</c> shipments with nothing newer than 7 days among <c>last_tracking_at</c>, <c>handed_over_at</c> and the failure's
    /// <c>dismissed_at</c> are flagged, one transaction each; the conditions are checked again under the row lock.
    /// </summary>
    internal async Task RunPossiblyLostAsync(CancellationToken ct)
    {
        string[] open = [ShipmentStatuses.HandedOver, ShipmentStatuses.InTransit];
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            var cutoff = clock.GetUtcNow() - Silence;
            due = await db.Database.SqlQuery<Guid>($"""
                SELECT s.id AS "Value" FROM shipments s LEFT JOIN delivery_failures f ON f.shipment_id = s.id
                WHERE s.direction = {Directions.Outbound} AND s.status = ANY({open}) AND greatest(s.last_tracking_at, s.handed_over_at, f.dismissed_at) < {cutoff}
                """).ToListAsync(ct);
        }

        foreach (var id in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var cutoff = clock.GetUtcNow() - Silence;
            var shipment = (await db.Shipments.FromSql($"""
                SELECT s.* FROM shipments s LEFT JOIN delivery_failures f ON f.shipment_id = s.id
                WHERE s.id = {id} AND s.status = ANY({open}) AND greatest(s.last_tracking_at, s.handed_over_at, f.dismissed_at) < {cutoff}
                FOR UPDATE OF s
                """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
            if (shipment is null) continue; // moved on since the scan
            await scope.ServiceProvider.GetRequiredService<Failures>().FlagAsync(shipment, ct);
            await tx.CommitAsync(ct);
        }
    }
}
