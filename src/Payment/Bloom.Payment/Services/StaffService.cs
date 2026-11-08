using Bloom.Payment.Context;
using Bloom.Payment.Data;
using Bloom.Payment.Domain;
using Bloom.Payment.Errors;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Services;

/// <summary>04-payment §5.1 staff API: Support and Admin only (plan R7), read-only.</summary>
public sealed class StaffService(PaymentDbContext db) : PaymentStaff.PaymentStaffBase
{
    public override async Task<GetPaymentResponse> GetPayment(GetPaymentRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Staff); // before validation (plan R7)
        if (!Guid.TryParseExact(request.OrderId, "D", out var orderId)) throw PaymentErrors.Validation("order_id", "order_id must be a UUID.");
        var ct = context.CancellationToken;

        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == orderId, ct) ?? throw PaymentErrors.NotFound("The payment");
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PaymentId == payment.Id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var disputes = await db.Disputes.AsNoTracking().Where(d => d.PaymentId == payment.Id).OrderBy(d => d.OpenedAt).ThenBy(d => d.Id).ToListAsync(ct);
        return new GetPaymentResponse { Payment = Views.Of(payment), Refunds = { refunds.Select(Views.Of) }, Disputes = { disputes.Select(Views.Of) } };
    }
}
