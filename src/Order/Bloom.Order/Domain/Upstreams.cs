using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Grpc.Core;

namespace Bloom.Order.Domain;

/// <summary>Calls to the services order depends on (plan R10, 03-order §5.2): 5 s deadline each. The checkout calls join as their tasks arrive.</summary>
public sealed class Upstreams(CatalogInternal.CatalogInternalClient catalog, InventoryStock.InventoryStockClient inventory)
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
}
