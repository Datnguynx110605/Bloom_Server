using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Observability;
using Bloom.Fulfillment.Shippo;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Worker;

/// <summary>
/// Buys and voids Shippo labels (05-fulfillment §6.6, plan R14, R15), every 5 s. Each shipment is handled in its own transaction that holds the row
/// <c>FOR UPDATE SKIP LOCKED</c>, Shippo calls included: a Support cancel (<c>CancelPackTask</c>) waits on that lock and then sees <c>label_ready</c> (→ <c>void_pending</c>).
/// </summary>
public sealed class LabelWorker(IServiceScopeFactory scopes, TimeProvider clock, FulfillmentMetrics metrics, FulfillmentOptions options, ILogger<LabelWorker> logger) : BackgroundService
{
    // Retry delay after failure n (1-based); every later failure waits as long as the last (plan R14).
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private const int AlertAttempt = 5;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5); // Shippo stamps labels with its own clock: look back a little further than the shipment's creation

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Label worker pass failed; retrying in 5 s");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Buys every due <c>label_pending</c> shipment, then voids every <c>void_pending</c> one.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var tried = new List<Guid>(); // a row whose failure couldn't be recorded stays due: try each shipment once per pass, so it neither spins nor blocks the rows behind it (F-2)
        while (await BuyOneAsync(tried, ct)) { }
        var attempted = new List<Guid>(); // a failed void stays void_pending: try each shipment once per pass, the next tick retries
        while (await VoidOneAsync(attempted, ct)) { }
    }

    private async Task<bool> BuyOneAsync(List<Guid> tried, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var shippo = scope.ServiceProvider.GetRequiredService<IShippo>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var ids = tried.ToArray();
        var shipment = (await db.Shipments.FromSql($"""
            SELECT * FROM shipments
            WHERE status = {ShipmentStatuses.LabelPending} AND next_attempt_at <= {now} AND id <> ALL({ids})
            ORDER BY next_attempt_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (shipment is null) return false;
        tried.Add(shipment.Id);

        try
        {
            var metadata = ShippoClient.MetadataPrefix + shipment.Id.ToString("D");
            var found = (await shippo.ListLabelsAsync(shipment.CreatedAt - ClockSkew, ct)).FirstOrDefault(l => l.Metadata == metadata)?.Validated(); // lookup before buy (FR-05); only the matched label is validated (F-1)
            var label = found ?? await shippo.BuyLabelAsync(await RequestAsync(db, shipment, ct), ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE shipments
                SET status = {ShipmentStatuses.LabelReady}, shippo_transaction_id = {label.TransactionId}, tracking_number = {label.TrackingNumber}, label_url = {label.LabelUrl},
                    label_attempts = 0, version = version + 1
                WHERE id = {shipment.Id} AND status = {ShipmentStatuses.LabelPending}
                """, ct);
            if (shipment.Direction == Directions.Return) await IssueReturnLabelAsync(scope.ServiceProvider, db, shipment, label.LabelUrl, now, ct);
            await tx.CommitAsync(ct);
            metrics.Label(found is not null ? "reused" : "bought");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Any failure of this row (Shippo, an unreadable address, ...) is a failed attempt, so a poison row backs off and the next row is still served.
            // The row lock is released first; the bookkeeping runs in a fresh transaction.
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollback)
            {
                logger.LogWarning(rollback, "Rolling back the label transaction of shipment {ShipmentId} failed; its lock went with the connection", shipment.Id);
            }
            await FailAsync(shipment, e, ct);
        }
        return true;
    }

    /// <summary>R14: a return label also takes the request <c>requested → label_issued</c> (lock order: shipments → return_requests) and publishes <c>return-label-issued</c>.</summary>
    private static async Task IssueReturnLabelAsync(IServiceProvider services, FulfillmentDbContext db, Shipment shipment, string labelUrl, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE return_requests SET status = {ReturnStatuses.LabelIssued}, version = version + 1, updated_at = {now}
            WHERE id = {shipment.ReturnRequestId} AND status = {ReturnStatuses.Requested}
            """, ct) != 1)
            throw new InvalidOperationException($"Return {shipment.ReturnRequestId} is not requested although its label was just bought; the expiry takes the shipment's lock first."); // cannot happen
        services.GetRequiredService<OutboxWriter>().PublishEvent(FulfillmentMessages.ReturnLabelIssued, new ReturnLabelIssuedV1(shipment.OrderId, shipment.ReturnRequestId!.Value, labelUrl));
        await db.SaveChangesAsync(ct);
    }

    private async Task<LabelRequest> RequestAsync(FulfillmentDbContext db, Shipment shipment, CancellationToken ct)
    {
        // The order's own task holds the address (spec §4). A plain read: it doesn't wait for the cancel that may hold that task.
        var json = await db.PackTasks.AsNoTracking().Where(p => p.OrderId == shipment.OrderId && p.Kind == TaskKinds.Order).Select(p => p.ShipTo).SingleAsync(ct)
            ?? throw new InvalidOperationException($"Order {shipment.OrderId} has no ship-to address.");
        var shipTo = JsonSerializer.Deserialize<ShipToV1>(json, JsonSerializerOptions.Web)!;
        var w = options.Warehouse;
        var token = shipment.Direction == Directions.Outbound && shipment.Service == ShippingOptions.Express ? options.Shippo.ExpressToken : options.Shippo.StandardToken;
        var warehouse = new ShippoAddress(w.Name, w.Street1, null, w.City, w.State, w.Zip, w.Phone);
        var customer = new ShippoAddress(shipTo.Name, shipTo.Lines.FirstOrDefault() ?? "", shipTo.Lines.Count > 1 ? string.Join(", ", shipTo.Lines.Skip(1)) : null, shipTo.City, shipTo.State, shipTo.Zip, shipTo.Phone);
        var isReturn = shipment.Direction == Directions.Return; // R13: a return goes ship_to → warehouse
        return new LabelRequest(shipment.Id, isReturn ? customer : warehouse, isReturn ? warehouse : customer, token, isReturn);
    }

    private async Task FailAsync(Shipment shipment, Exception e, CancellationToken ct)
    {
        var n = shipment.LabelAttempts + 1;
        DateTimeOffset next;
        for (var attempt = 1; ; attempt++) // a database failure here would leave the row first in line: one more try, then give up for this pass (F-2)
        {
            next = clock.GetUtcNow() + Backoff[Math.Min(n, Backoff.Length) - 1]; // from now, after the Shippo call (F-3)
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE shipments SET label_attempts = {n}, next_attempt_at = {next}, version = version + 1
                    WHERE id = {shipment.Id} AND status = {ShipmentStatuses.LabelPending}
                    """, ct);
                await tx.CommitAsync(ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == 2)
                {
                    logger.LogError(ex, "Recording the failed label attempt of shipment {ShipmentId} failed; it is tried again on the next pass", shipment.Id);
                    return;
                }
            }
        }
        if (n < AlertAttempt) logger.LogWarning(e, "Label for shipment {ShipmentId} failed (attempt {Attempt}); retrying at {NextAttemptAt}", shipment.Id, n, next);
        else if (n == AlertAttempt)
        {
            logger.LogError(e, "Label for shipment {ShipmentId} failed {Attempt} times; retrying every 2 minutes", shipment.Id, n);
            metrics.Label("failed");
        }
    }

    private async Task<bool> VoidOneAsync(List<Guid> attempted, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var shippo = scope.ServiceProvider.GetRequiredService<IShippo>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = attempted.ToArray();
        var shipment = (await db.Shipments.FromSql($"""
            SELECT * FROM shipments
            WHERE status = {ShipmentStatuses.VoidPending} AND id <> ALL({ids})
            ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (shipment is null) return false;
        attempted.Add(shipment.Id);

        try
        {
            await shippo.VoidLabelAsync(shipment.ShippoTransactionId ?? throw new InvalidOperationException($"Shipment {shipment.Id} is void_pending without a transaction."), ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE shipments SET status = {ShipmentStatuses.Voided}, version = version + 1
                WHERE id = {shipment.Id} AND status = {ShipmentStatuses.VoidPending}
                """, ct);
            await tx.CommitAsync(ct);
            metrics.Label("voided");
        }
        catch (ShippoException e)
        {
            logger.LogWarning(e, "Voiding the label of shipment {ShipmentId} failed; retrying on the next pass", shipment.Id);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Voiding the label of shipment {ShipmentId} failed unexpectedly; retrying on the next pass", shipment.Id); // the other void_pending rows are still served
        }
        return true;
    }
}
