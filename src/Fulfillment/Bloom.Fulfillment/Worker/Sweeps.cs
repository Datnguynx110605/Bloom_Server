using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Worker;

/// <summary>Hourly sweeps (05-fulfillment §6.3, §6.4, spec §7). A later task adds the daily label-check pass to the same loop.</summary>
public sealed class Sweeps(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Sweeps> logger) : BackgroundService
{
    private static readonly TimeSpan Silence = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            await PassAsync("Possibly-lost", RunPossiblyLostAsync, ct);
            await PassAsync("Return-expiry", RunReturnExpiryAsync, ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    // One failing pass must not skip the others.
    private async Task PassAsync(string name, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        try
        {
            await pass(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Sweep} sweep failed; retrying in 1 h", name);
        }
    }

    /// <summary>
    /// <c>requested</c> / <c>label_issued</c> requests past <c>expires_at</c> (RET-R15, spec §7), one transaction each: lock the shipment, guarded request → <c>expired</c>
    /// (checked again under the lock), the shipment's label taken away (plan R15) and <c>return-closed</c>. <c>in_transit</c> and later never expire, nor does a request whose return shipment the carrier has scanned.
    /// </summary>
    internal async Task RunReturnExpiryAsync(CancellationToken ct)
    {
        string[] open = [ReturnStatuses.Requested, ReturnStatuses.LabelIssued];
        string[] scanned = [ShipmentStatuses.InTransit, ShipmentStatuses.Delivered]; // the carrier has the label: RET-R15 is "label not scanned within 14 days"
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            var now = clock.GetUtcNow();
            due = await db.Database.SqlQuery<Guid>($"""
                SELECT r.id AS "Value" FROM return_requests r
                WHERE r.status = ANY({open}) AND r.expires_at < {now}
                  AND NOT EXISTS (SELECT 1 FROM shipments s WHERE s.return_request_id = r.id AND s.direction = {Directions.Return} AND s.status = ANY({scanned}))
                """).ToListAsync(ct);
        }

        foreach (var id in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM shipments WHERE return_request_id = {id} AND direction = {Directions.Return} FOR UPDATE", ct); // waits for the label worker
            var orderId = (await db.Database.SqlQuery<Guid>($"""
                UPDATE return_requests SET status = {ReturnStatuses.Expired}, version = version + 1, updated_at = {now}
                WHERE id = {id} AND status = ANY({open}) AND expires_at < {now}
                  AND NOT EXISTS (SELECT 1 FROM shipments s WHERE s.return_request_id = {id} AND s.direction = {Directions.Return} AND s.status = ANY({scanned}))
                RETURNING order_id AS "Value"
                """).ToListAsync(ct)).SingleOrDefault();
            if (orderId == Guid.Empty) continue; // moved on since the scan
            await db.Database.ExecuteSqlAsync($"""
                UPDATE shipments
                SET status = CASE WHEN status = {ShipmentStatuses.LabelReady} THEN {ShipmentStatuses.VoidPending} ELSE {ShipmentStatuses.Voided} END, version = version + 1
                WHERE return_request_id = {id} AND direction = {Directions.Return} AND status IN ({ShipmentStatuses.LabelPending}, {ShipmentStatuses.LabelReady})
                """, ct);
            scope.ServiceProvider.GetRequiredService<OutboxWriter>().PublishEvent(FulfillmentMessages.ReturnClosed, new ReturnClosedV1(orderId, id, "expired"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
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
