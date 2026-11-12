using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Errors;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Domain;

/// <summary>
/// <c>delivery_failures</c> rows (plan R17, spec §4). Every method assumes the caller is in a transaction that holds the shipment's row lock
/// (lock order: shipments → delivery_failures → …), so the guarded updates below cannot race.
/// </summary>
public sealed class Failures(FulfillmentDbContext db, TimeProvider clock, ILogger<Failures> logger)
{
    /// <summary>
    /// The shipment's and then its failure's row locks (lock order: shipments → delivery_failures), for the staff methods that read the failure's status before acting on it.
    /// <c>NOT_FOUND</c> for an unknown shipment; the failure is null when it has none.
    /// </summary>
    public async Task<(Shipment Shipment, DeliveryFailure? Failure)> LockAsync(Guid shipmentId, CancellationToken ct)
    {
        var shipment = (await db.Shipments.FromSql($"SELECT * FROM shipments WHERE id = {shipmentId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
            ?? throw FulfillmentErrors.NotFound("The shipment");
        var failure = (await db.DeliveryFailures.FromSql($"SELECT * FROM delivery_failures WHERE shipment_id = {shipmentId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        return (shipment, failure);
    }

    /// <summary>Shipment → <c>possibly_lost</c> and failure upsert (<c>kind = lost</c>, <c>previous_status</c> = the shipment's status); an existing <c>dismissed</c> row is reused.</summary>
    public async Task FlagAsync(Shipment s, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var moved = await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments SET status = {ShipmentStatuses.PossiblyLost}, version = version + 1
            WHERE id = {s.Id} AND status = {s.Status}
            """, ct);
        if (moved != 1) throw new InvalidOperationException($"Shipment {s.Id} is no longer {s.Status}; the caller must hold its lock.");
        await UpsertAsync(s.Id, FailureKinds.Lost, FailureStatuses.PossiblyLost, s.Status, now, ct);
        logger.LogError("Shipment {ShipmentId} of order {OrderId} was {Status} and is now possibly lost; Support must review it", s.Id, s.OrderId, s.Status);
    }

    /// <summary><c>RETURNED</c>: <c>kind = returned_to_sender</c>, <c>status = confirmed</c> (the shipment's own status is the caller's).</summary>
    public Task ConfirmReturnedAsync(Guid shipmentId, CancellationToken ct) =>
        UpsertAsync(shipmentId, FailureKinds.ReturnedToSender, FailureStatuses.Confirmed, null, clock.GetUtcNow(), ct);

    /// <summary>Failure → <c>dismissed</c> (<c>dismissed_at</c> = now) and the shipment goes back to <c>previous_status</c>. Manual and automatic dismissal alike.</summary>
    public async Task DismissAsync(Guid shipmentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var previous = (await db.Database.SqlQuery<string?>($"""
            UPDATE delivery_failures SET status = {FailureStatuses.Dismissed}, dismissed_at = {now}, updated_at = {now}
            WHERE shipment_id = {shipmentId} AND status = {FailureStatuses.PossiblyLost}
            RETURNING previous_status AS "Value"
            """).ToListAsync(ct)).SingleOrDefault()
            ?? throw new InvalidOperationException($"Shipment {shipmentId} has no open possibly-lost failure.");
        var moved = await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments SET status = {previous}, version = version + 1
            WHERE id = {shipmentId} AND status = {ShipmentStatuses.PossiblyLost}
            """, ct);
        if (moved != 1) throw new InvalidOperationException($"Shipment {shipmentId} is not possibly_lost.");
    }

    private async Task UpsertAsync(Guid shipmentId, string kind, string status, string? previousStatus, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO delivery_failures (shipment_id, kind, status, previous_status, created_at, updated_at)
            VALUES ({shipmentId}, {kind}, {status}, {previousStatus}, {now}, {now})
            ON CONFLICT (shipment_id) DO UPDATE
            SET kind = EXCLUDED.kind, status = EXCLUDED.status, previous_status = EXCLUDED.previous_status, updated_at = EXCLUDED.updated_at
            """, ct);
}
