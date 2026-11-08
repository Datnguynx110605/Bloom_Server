using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Errors;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Payment.Services;

/// <summary>04-payment §5.1 internal API (order calls it; no HTTP route, no role check: Linkerd guards it, §9).</summary>
public sealed class InternalService(PaymentDbContext db, IPaymentProvider provider, TimeProvider clock, PaymentMetrics metrics) : PaymentInternal.PaymentInternalBase
{
    private static readonly TimeSpan Lock = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(47); // 15-min checkout + Stripe's 30-min minimum + 2-min margin (spec §4)

    /// <summary>§6.1, LLD-ADR-001: Tx 1 claims the order, Stripe is called outside any transaction, Tx 2 stores the session. Every statement is its own transaction.</summary>
    public override async Task<CreatePaymentResponse> CreatePayment(CreatePaymentRequest request, ServerCallContext context)
    {
        Validate(request);
        var ct = context.CancellationToken;
        var orderId = Guid.Parse(request.OrderId);

        var found = await FindAsync(orderId, ct);
        if (found is null)
        {
            PaymentRow? claimed = null;
            try
            {
                claimed = await InsertAsync(request, ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // another call inserted first: step 1 once more
            }
            if (claimed is not null) return await OpenSessionAsync(claimed, request, ct);
            found = await FindAsync(orderId, ct) ?? throw new InvalidOperationException("ux_payments_order_id fired but the payment is gone.");
        }

        if (found.AmountCents != request.AmountCents)
            throw Fail("different_amount", PaymentErrors.ExistsDifferentAmount, "A payment for this order exists with another amount.");
        if (found.Status != PaymentStatuses.Creating)
        {
            metrics.Created("existing");
            return Response(found.Id, found.CheckoutUrl, found.SessionExpiresAt);
        }
        if (found.LockedUntil > clock.GetUtcNow())
            throw Fail("in_progress", PaymentErrors.CreateInProgress, "The payment for this order is being created.");

        // Stale `creating`: take over, then resume with the stored expiry so Stripe sees the identical request.
        var now = clock.GetUtcNow();
        var until = now + Lock;
        var taken = await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET locked_until = {until}, version = version + 1, updated_at = {now}
            WHERE id = {found.Id} AND status = 'creating' AND locked_until <= {now}
            """, ct);
        if (taken != 1) throw Fail("in_progress", PaymentErrors.CreateInProgress, "The payment for this order is being created.");
        found.LockedUntil = until;
        return await OpenSessionAsync(found, request, ct);
    }

    private Task<PaymentRow?> FindAsync(Guid orderId, CancellationToken ct) =>
        db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == orderId, ct);

    /// <summary>Tx 1.</summary>
    private async Task<PaymentRow> InsertAsync(CreatePaymentRequest r, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = new PaymentRow
        {
            Id = Guid.CreateVersion7(),
            OrderId = Guid.Parse(r.OrderId),
            OrderNumber = r.OrderNumber,
            AccountId = Guid.Parse(r.AccountId),
            AmountCents = r.AmountCents,
            Currency = "usd",
            Status = PaymentStatuses.Creating,
            LockedUntil = now + Lock,
            SessionExpiresAt = now + SessionLife,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Payments.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.Entry(row).State = EntityState.Detached; // the caller may look the row up again on this context
            throw;
        }
        return row;
    }

    /// <summary>Steps 3 and 4 for a row this call has claimed (its <c>LockedUntil</c> is the claim).</summary>
    private async Task<CreatePaymentResponse> OpenSessionAsync(PaymentRow row, CreatePaymentRequest r, CancellationToken ct)
    {
        StripeSession session;
        try
        {
            session = await provider.CreateSessionAsync(
                new SessionRequest(row.OrderId, row.Id, row.OrderNumber, row.SessionExpiresAt!.Value, [.. r.DisplayLines.Select(l => (l.Name, l.AmountCents))]), ct);
        }
        catch (ProviderException)
        {
            // Owner item 2: release our own claim so the next call resumes at once. A crash never gets here and waits out the 60 s.
            var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE payments SET locked_until = {now}, version = version + 1, updated_at = {now}
                WHERE id = {row.Id} AND locked_until = {row.LockedUntil}
                """, ct);
            throw Fail("provider_unavailable", PaymentErrors.ProviderUnavailable, "The payment provider is unavailable.");
        }

        // Tx 2: `open` only from `creating`, so an early `completed` webhook keeps `succeeded` (spec §7).
        var saved = await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET stripe_session_id = {session.Id}, checkout_url = {session.Url}, session_expires_at = {session.ExpiresAt},
                status = CASE status WHEN 'creating' THEN 'open' ELSE status END, locked_until = NULL, version = version + 1, updated_at = {clock.GetUtcNow()}
            WHERE id = {row.Id}
            """, ct);
        if (saved != 1) throw new InvalidOperationException($"Payment {row.Id} vanished before its session was stored.");
        metrics.Created("created");
        return Response(row.Id, session.Url, session.ExpiresAt);
    }

    private RpcException Fail(string result, string reason, string message)
    {
        metrics.Created(result);
        return PaymentErrors.Error(reason, message);
    }

    private static CreatePaymentResponse Response(Guid id, string? url, DateTimeOffset? expiresAt) => new()
    {
        PaymentId = id.ToString("D"),
        CheckoutUrl = url ?? "",
        SessionExpiresAt = expiresAt is { } e ? Timestamp.FromDateTimeOffset(e) : null,
    };

    /// <summary>Plan R9; spec §6.</summary>
    private static void Validate(CreatePaymentRequest r)
    {
        if (!Guid.TryParseExact(r.OrderId, "D", out _)) throw PaymentErrors.Validation("order_id", "order_id must be a UUID.");
        if (!Guid.TryParseExact(r.AccountId, "D", out _)) throw PaymentErrors.Validation("account_id", "account_id must be a UUID.");
        if (r.AmountCents <= 0) throw PaymentErrors.Validation("amount_cents", "amount_cents must be positive.");
        if (r.DisplayLines.Count == 0) throw PaymentErrors.Validation("display_lines", "display_lines must not be empty.");
        if (r.DisplayLines.Any(l => l.AmountCents < 0)) throw PaymentErrors.Validation("display_lines", "A display line amount must not be negative.");
        if (r.DisplayLines.Sum(l => (decimal)l.AmountCents) != r.AmountCents) throw PaymentErrors.Validation("display_lines", "display_lines must add up to amount_cents.");
    }
}
