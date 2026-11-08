using Bloom.Payment.Contracts;
using Bloom.Payment.Errors;
using Grpc.Core;

namespace Bloom.Payment.Services;

/// <summary>04-payment §5.1 internal API (order calls it; no HTTP route, no role check: Linkerd guards it, §9).</summary>
public sealed class InternalService : PaymentInternal.PaymentInternalBase
{
    public override Task<CreatePaymentResponse> CreatePayment(CreatePaymentRequest request, ServerCallContext context)
    {
        Validate(request);
        throw new NotImplementedException("CreatePayment arrives in plan Task 4.");
    }

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
