using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Observability;
using Bloom.Fulfillment.Shippo;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Services;

/// <summary>
/// <c>POST /webhooks/shippo/{secret}</c> (05-fulfillment §6.3, plan R16). The body only names the tracking number; the status is always re-fetched from Shippo (FR-04).
/// Returns 404 for a wrong secret, 413 above 64 KB, 400 without <c>data.tracking_number</c>, 500 when Shippo can't be reached (Shippo retries), else 200.
/// </summary>
public static class ShippoWebhook
{
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly string[] Terminal = [ShipmentStatuses.Delivered, ShipmentStatuses.Lost, ShipmentStatuses.ReturnedToSender, ShipmentStatuses.VoidPending, ShipmentStatuses.Voided];

    private const string Route = "/webhooks/shippo/{secret}";

    public static IEndpointRouteBuilder MapShippoWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, HandleAsync);
        return app;
    }

    /// <summary>The secret is part of the URL: spans of this route must not carry it in <c>url.path</c> (the span name already uses the route template).</summary>
    internal static void HideSecret(Activity activity, HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/webhooks/shippo")) activity.SetTag("url.path", Route);
    }

    private static async Task<IResult> HandleAsync(
        string secret, HttpRequest request, FulfillmentOptions options, FulfillmentMetrics metrics, IShippo shippo, FulfillmentDbContext db, Failures failures, Handover handover,
        OutboxWriter outbox, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(options.Shippo.WebhookSecret)))
        {
            metrics.Webhook("bad_secret");
            return Results.NotFound();
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var length = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        if (length > MaxBodyBytes)
        {
            metrics.Webhook("invalid");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var tracking = TrackingNumber(buffer.AsMemory(0, length));
        if (tracking is null)
        {
            metrics.Webhook("invalid");
            return Results.BadRequest();
        }

        var shipmentId = await db.Shipments.AsNoTracking().Where(s => s.TrackingNumber == tracking).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
        if (shipmentId is null)
        {
            metrics.Webhook("unknown");
            return Results.Ok();
        }

        var logger = loggers.CreateLogger("Bloom.Fulfillment.ShippoWebhook");
        try
        {
            var status = await shippo.GetTrackingStatusAsync(tracking, ct); // FR-04
            var changed = await ApplyAsync(shipmentId.Value, status, db, failures, handover, outbox, clock.GetUtcNow(), logger, ct);
            metrics.Webhook(changed ? "applied" : "unchanged");
            return Results.Ok();
        }
        catch (ShippoException e)
        {
            logger.LogWarning(e, "Re-fetching the tracking status of {TrackingNumber} failed", tracking);
            metrics.Webhook("error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        catch
        {
            metrics.Webhook("error");
            throw;
        }
    }

    private static string? TrackingNumber(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var number = doc.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("tracking_number", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            return number.Length == 0 ? null : number;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Locks the shipment, then applies the re-fetched status. Returns whether the shipment's status changed.</summary>
    private static async Task<bool> ApplyAsync(
        Guid id, string tracking, FulfillmentDbContext db, Failures failures, Handover handover, OutboxWriter outbox, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // D32 may hand the task over, so the task's lock comes first (lock order: pack_tasks → shipments, as HandOver and CancelPackTask take them)
        var labelTaskId = await db.Shipments.AsNoTracking().Where(x => x.Id == id && x.Direction == Directions.Outbound && x.Status == ShipmentStatuses.LabelReady).Select(x => x.PackTaskId).SingleOrDefaultAsync(ct);
        if (labelTaskId is { } lockedTask) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM pack_tasks WHERE id = {lockedTask} FOR UPDATE", ct);
        var s = (await db.Shipments.FromSql($"SELECT * FROM shipments WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct)).Single(); // first lock of the order
        if (Terminal.Contains(s.Status))
        {
            logger.LogWarning("Shippo reported {Tracking} for shipment {ShipmentId}, already {Status}; ignored", tracking, s.Id, s.Status);
            return false;
        }

        var to = Target(s, tracking);
        if (s.Direction == Directions.Outbound && s.Status == ShipmentStatuses.LabelReady && to is ShipmentStatuses.InTransit or ShipmentStatuses.Delivered) // D32: a carrier scan implies the handover
        {
            var task = s.PackTaskId is { } taskId ? await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == taskId, ct) : null;
            if (!await handover.ApplyAsync(s, task, now, ct))
            {
                logger.LogWarning("Shippo reported {Tracking} on label_ready shipment {ShipmentId} but its task is not packed; ignored", tracking, s.Id);
                return false;
            }
        }
        if (s.Status == ShipmentStatuses.PossiblyLost && to is ShipmentStatuses.InTransit or ShipmentStatuses.Delivered) await failures.DismissAsync(s.Id, ct); // late news (§8)
        var settle = to == ShipmentStatuses.PossiblyLost ? null : to; // Failures.FlagAsync moves that one
        var deliveredAt = to == ShipmentStatuses.Delivered ? (DateTimeOffset?)now : null;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments
            SET last_tracking_at = {now}, status = COALESCE({settle}, status), delivered_at = COALESCE({deliveredAt}, delivered_at), version = version + 1
            WHERE id = {s.Id}
            """, ct);

        switch (to)
        {
            case ShipmentStatuses.PossiblyLost:
                await failures.FlagAsync(s, ct);
                break;
            case ShipmentStatuses.ReturnedToSender:
                await failures.ConfirmReturnedAsync(s.Id, ct);
                break;
            case ShipmentStatuses.InTransit when s.Direction == Directions.Return: // lock order: shipments → return_requests
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE return_requests SET status = {ReturnStatuses.InTransit}, version = version + 1, updated_at = {now}
                    WHERE id = {s.ReturnRequestId} AND status = {ReturnStatuses.LabelIssued}
                    """, ct);
                break;
            case ShipmentStatuses.Delivered when s.Direction == Directions.Outbound:
                await DeliveredAsync(s, db, outbox, now, ct);
                break;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return to is not null;
    }

    // Return shipments follow the label and the carrier (label_ready → in_transit → delivered); their request is the warehouse's to receive, so only TRANSIT moves it.
    private static string? Target(Shipment s, string tracking) => (s.Direction, tracking) switch
    {
        (Directions.Outbound, "TRANSIT") when s.Status is ShipmentStatuses.LabelReady or ShipmentStatuses.HandedOver or ShipmentStatuses.PossiblyLost => ShipmentStatuses.InTransit,
        (Directions.Outbound, "DELIVERED") when s.Status is ShipmentStatuses.LabelReady or ShipmentStatuses.HandedOver or ShipmentStatuses.InTransit or ShipmentStatuses.PossiblyLost => ShipmentStatuses.Delivered,
        (Directions.Outbound, "RETURNED") when s.Status is ShipmentStatuses.HandedOver or ShipmentStatuses.InTransit or ShipmentStatuses.PossiblyLost => ShipmentStatuses.ReturnedToSender,
        (Directions.Outbound, "FAILURE") when s.Status is ShipmentStatuses.HandedOver or ShipmentStatuses.InTransit => ShipmentStatuses.PossiblyLost,
        (Directions.Return, "TRANSIT") when s.Status == ShipmentStatuses.LabelReady => ShipmentStatuses.InTransit,
        (Directions.Return, "DELIVERED") when s.Status is ShipmentStatuses.LabelReady or ShipmentStatuses.InTransit => ShipmentStatuses.Delivered,
        (Directions.ShipBack, "TRANSIT") when s.Status == ShipmentStatuses.HandedOver => ShipmentStatuses.InTransit,
        (Directions.ShipBack, "DELIVERED") when s.Status is ShipmentStatuses.HandedOver or ShipmentStatuses.InTransit => ShipmentStatuses.Delivered,
        _ => null,
    };

    /// <summary>Order delivered, its pending coverages active from now for their months, and <c>shipment-delivered</c> (lock order: order_accounts → warranty_coverages).</summary>
    private static async Task DeliveredAsync(Shipment s, FulfillmentDbContext db, OutboxWriter outbox, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"UPDATE order_accounts SET delivered_at = {now} WHERE order_id = {s.OrderId}", ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE warranty_coverages
            SET status = {CoverageStatuses.Active}, starts_at = {now}, ends_at = ({now} AT TIME ZONE 'UTC' + make_interval(months => coverage_months)) AT TIME ZONE 'UTC'
            WHERE order_id = {s.OrderId} AND status = {CoverageStatuses.PendingDelivery}
            """, ct);
        var refId = await db.PackTasks.AsNoTracking().Where(p => p.Id == s.PackTaskId).Select(p => p.RefId).SingleAsync(ct);
        outbox.PublishEvent(FulfillmentMessages.ShipmentDelivered, new ShipmentDeliveredV1(s.OrderId, refId, now));
    }
}
