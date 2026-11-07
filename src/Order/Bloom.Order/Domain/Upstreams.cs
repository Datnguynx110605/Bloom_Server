using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Context;
using Bloom.Payment.Contracts;
using Grpc.Core;

namespace Bloom.Order.Domain;

/// <summary>Calls to the services order depends on (plan R10, 03-order §5.2): 5 s deadline each. The checkout calls join as their tasks arrive.</summary>
public sealed class Upstreams(CatalogInternal.CatalogInternalClient catalog, InventoryStock.InventoryStockClient inventory, LaunchInternal.LaunchInternalClient launch, PaymentInternal.PaymentInternalClient payment)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>The catalog's answer per SKU. Any failure is <c>UNAVAILABLE</c>.</summary>
    public async Task<IReadOnlyDictionary<Guid, SkuPricing>> PricingAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        try
        {
            var response = await catalog.GetPricingAsync(new GetPricingRequest { SkuIds = { skuIds.Distinct().Select(id => id.ToString("D")) } }, deadline: DateTime.UtcNow + Deadline, cancellationToken: ct);
            return response.Items.ToDictionary(i => Guid.Parse(i.SkuId));
        }
        catch (RpcException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The catalog is unavailable."));
        }
    }

    /// <summary>The stock available per SKU, or <c>null</c> when inventory fails: the cart is still shown, without stock flags.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>?> AvailabilityAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        try
        {
            var response = await inventory.GetAvailabilityAsync(new GetAvailabilityRequest { SkuIds = { skuIds.Distinct().Select(id => id.ToString("D")) } }, deadline: DateTime.UtcNow + Deadline, cancellationToken: ct);
            return response.Items.ToDictionary(i => Guid.Parse(i.SkuId), i => i.Available);
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>
    /// Inventory's <c>ReserveStock</c> (idempotent by order). A <c>bloom</c> rejection (<c>SOLD_OUT</c>, <c>RESERVATION_CONFLICT</c>, …) is rethrown as it came (plan R10).
    /// A plain status that the idempotency guard would store as an answer is <c>UNAVAILABLE</c> here, so the key is released instead.
    /// </summary>
    public async Task ReserveAsync(Guid orderId, IEnumerable<(Guid SkuId, int Quantity)> lines, CancellationToken ct)
    {
        var request = new ReserveStockRequest { OrderId = orderId.ToString("D") };
        request.Lines.AddRange(lines.Select(l => new StockLine { SkuId = l.SkuId.ToString("D"), Quantity = l.Quantity }));
        try
        {
            await inventory.ReserveStockAsync(request, deadline: DateTime.UtcNow + Deadline, cancellationToken: ct);
        }
        catch (RpcException e) when (IsRejection(e) && !IsBloom(e))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Inventory failed."));
        }
    }

    /// <summary>
    /// Launch's <c>HoldAllocation</c> (idempotent by order), with the customer's <c>x-bloom-*</c> headers: launch checks the pass against that identity (plan R10). Rejections pass through
    /// as in <see cref="ReserveAsync"/>.
    /// </summary>
    public async Task HoldAsync(Guid orderId, string passToken, IEnumerable<(Guid SkuId, int Quantity)> lines, Caller caller, CancellationToken ct)
    {
        var request = new HoldAllocationRequest { OrderId = orderId.ToString("D"), PassToken = passToken };
        request.Lines.AddRange(lines.Select(l => new HoldLine { SkuId = l.SkuId.ToString("D"), Quantity = l.Quantity }));
        var headers = new Metadata { { "x-bloom-user-id", caller.UserId.ToString("D") }, { "x-bloom-user-type", caller.UserType } };
        if (caller.Role is not null) headers.Add("x-bloom-role", caller.Role);
        try
        {
            await launch.HoldAllocationAsync(request, headers, DateTime.UtcNow + Deadline, ct);
        }
        catch (RpcException e) when (IsRejection(e) && !IsBloom(e))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Launch failed."));
        }
    }

    /// <summary>Payment's <c>CreatePayment</c> (idempotent by order). Failures propagate; the caller decides (owner item 3).</summary>
    public async Task<CreatePaymentResponse> CreatePaymentAsync(Guid orderId, string number, Guid accountId, long totalCents, IEnumerable<DisplayLine> displayLines, CancellationToken ct)
    {
        var request = new CreatePaymentRequest { OrderId = orderId.ToString("D"), OrderNumber = number, AccountId = accountId.ToString("D"), AmountCents = totalCents };
        request.DisplayLines.AddRange(displayLines);
        return await payment.CreatePaymentAsync(request, deadline: DateTime.UtcNow + Deadline, cancellationToken: ct);
    }

    /// <summary>The statuses the idempotency guard stores and replays as the answer (spec §6.7).</summary>
    public static bool IsRejection(RpcException e) =>
        e.StatusCode is StatusCode.FailedPrecondition or StatusCode.InvalidArgument or StatusCode.NotFound or StatusCode.AlreadyExists or StatusCode.PermissionDenied;

    private static bool IsBloom(RpcException e) =>
        e.GetRpcStatus()?.Details.Any(d => d.Is(Google.Rpc.ErrorInfo.Descriptor) && d.Unpack<Google.Rpc.ErrorInfo>().Domain == "bloom") == true;
}
