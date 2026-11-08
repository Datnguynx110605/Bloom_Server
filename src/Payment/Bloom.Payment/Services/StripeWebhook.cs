using System.Text.Json;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Domain;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Services;

/// <summary>
/// <c>POST /webhooks/stripe</c> (04-payment §6.2, PR-03; spec §7; owner item 3). One transaction per request. 413 above 256 KB, 400 for a bad signature or a body that is not
/// an event, 200 once stored (and applied), 500 on any processing error so Stripe retries.
/// </summary>
public static class StripeWebhook
{
    private const int MaxBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapStripeWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/stripe", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request, PaymentOptions options, PaymentMetrics metrics, PaymentDbContext db, Transitions transitions, OutboxWriter outbox,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger("Bloom.Payment.StripeWebhook");
        var buffer = new byte[MaxBodyBytes + 1];
        var length = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        if (length > MaxBodyBytes)
        {
            logger.LogError("A Stripe webhook body above {Max} bytes was refused", MaxBodyBytes);
            metrics.Webhook("unknown", "error");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var body = buffer.AsMemory(0, length);

        if (!WebhookSignature.Verify(request.Headers["Stripe-Signature"], body.Span, options.Stripe.WebhookSecret, clock.GetUtcNow()))
        {
            logger.LogError("A Stripe webhook with a missing or invalid signature was refused");
            metrics.Webhook("unknown", "bad_signature");
            return Results.BadRequest();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Malformed(logger, metrics);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "id") is not { } eventId || Str(root, "type") is not { } type
                || Int(root, "created") is not { } created || created is < 0 or > 253402300799) // a Unix time DateTimeOffset can hold
                return Malformed(logger, metrics);

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var now = clock.GetUtcNow();
                var payload = System.Text.Encoding.UTF8.GetString(body.Span);
                var stored = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO provider_events (event_id, type, payload, received_at) VALUES ({eventId}, {type}, {payload}::jsonb, {now})
                    ON CONFLICT DO NOTHING
                    """, ct);
                if (stored == 0)
                {
                    metrics.Webhook(type, "duplicate");
                    return Results.Ok();
                }

                var obj = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
                if (obj.ValueKind == JsonValueKind.Object) await ApplyAsync(type, obj, created, db, transitions, outbox, clock, logger, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                metrics.Webhook(type, "processed");
                metrics.WebhookLag((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(created)).TotalSeconds);
                return Results.Ok();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Processing Stripe event {EventId} ({Type}) failed", eventId, type);
                metrics.Webhook(type, "error");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }

    private static IResult Malformed(ILogger logger, PaymentMetrics metrics)
    {
        logger.LogError("A signed Stripe webhook body is not an event with a string id, a string type and an integer created");
        metrics.Webhook("unknown", "error");
        return Results.BadRequest();
    }

    /// <summary>Spec §7 "Finding the record for an event": a miss leaves the event stored only.</summary>
    private static async Task ApplyAsync(
        string type, JsonElement obj, long created, PaymentDbContext db, Transitions transitions, OutboxWriter outbox, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        switch (type)
        {
            case "checkout.session.completed" when Str(obj, "payment_status") == "paid":
                if (PaymentId(obj) is { } paid)
                    await transitions.SucceedAsync(paid, Str(obj, "payment_intent") ?? throw new InvalidOperationException("A paid session has no payment_intent."), ct);
                break;

            case "checkout.session.expired":
                if (PaymentId(obj) is { } expired) await transitions.ExpireAsync(expired, ct);
                break;

            case "payment_intent.payment_failed":
                if (PaymentId(obj) is { } failed && await db.Payments.AsNoTracking().Where(p => p.Id == failed).Select(p => (Guid?)p.OrderId).SingleOrDefaultAsync(ct) is { } orderId)
                {
                    var error = obj.TryGetProperty("last_payment_error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                    var code = error.ValueKind == JsonValueKind.Object ? Str(error, "decline_code") ?? Str(error, "code") : null;
                    outbox.PublishEvent(PaymentMessages.Failed, new PaymentFailedV1(orderId, failed, code ?? "unknown"));
                }
                break;

            case "refund.updated" or "charge.refund.updated":
                await ApplyRefundAsync(obj, db, transitions, ct);
                break;

            case "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed":
                await UpsertDisputeAsync(obj, created, db, clock.GetUtcNow(), logger, ct);
                break;
        }
    }

    /// <summary>The refund is found by its Stripe id, else by <c>metadata.refund_id</c> (the executor may have crashed before storing the id).</summary>
    private static async Task ApplyRefundAsync(JsonElement refund, PaymentDbContext db, Transitions transitions, CancellationToken ct)
    {
        if (Str(refund, "id") is not { } stripeId || Str(refund, "status") is not { } status) return;
        var id = await db.Refunds.AsNoTracking().Where(r => r.StripeRefundId == stripeId).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct) ?? MetadataId(refund, "refund_id");
        if (id is { } found) await transitions.ApplyRefundAsync(found, status, stripeId, Str(refund, "failure_reason"), ct);
    }

    private static async Task UpsertDisputeAsync(JsonElement dispute, long eventCreated, PaymentDbContext db, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        if (Str(dispute, "payment_intent") is not { } intent) return; // a null would match every payment without an intent
        var paymentId = await db.Payments.AsNoTracking().Where(p => p.StripePaymentIntentId == intent).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (paymentId is null) return;

        var id = Str(dispute, "id") ?? throw new InvalidOperationException("A dispute has no id.");
        var status = Str(dispute, "status") ?? throw new InvalidOperationException($"Dispute {id} has no status.");
        var reason = Str(dispute, "reason") ?? throw new InvalidOperationException($"Dispute {id} has no reason.");
        var amount = Int(dispute, "amount") ?? throw new InvalidOperationException($"Dispute {id} has no amount.");
        var openedAt = DateTimeOffset.FromUnixTimeSeconds(Int(dispute, "created") ?? eventCreated);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO disputes (id, payment_id, stripe_dispute_id, status, amount_cents, reason, opened_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {paymentId}, {id}, {status}, {amount}, {reason}, {openedAt}, {now})
            ON CONFLICT (stripe_dispute_id) DO UPDATE SET status = EXCLUDED.status, amount_cents = EXCLUDED.amount_cents, reason = EXCLUDED.reason, updated_at = EXCLUDED.updated_at
            """, ct);
        logger.LogError("Stripe dispute {DisputeId} on payment {PaymentId} is {Status}", id, paymentId, status);
    }

    private static Guid? PaymentId(JsonElement obj) => MetadataId(obj, "payment_id");

    private static Guid? MetadataId(JsonElement obj, string key) =>
        obj.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object && Guid.TryParse(Str(m, key), out var id) ? id : null;

    private static long? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
