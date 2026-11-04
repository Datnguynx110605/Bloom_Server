using System.Collections.Concurrent;
using System.Net;
using Bloom.Catalog.Contracts;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bloom.Inventory.Tests;

/// <summary>The catalog's <c>LockSkuForStock</c> on Kestrel <c>127.0.0.1:0</c>, HTTP/2 without TLS (as identity's <c>FakeOrder</c>).</summary>
public sealed class FakeCatalog : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, (Guid ProductId, bool Serialized)> _skus = [];
    private readonly ConcurrentDictionary<Guid, int> _calls = [];
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    /// <summary>Calls fail with <c>Unavailable</c>.</summary>
    public bool Down { get; set; }

    public void AddSku(Guid skuId, Guid productId, bool serialized) => _skus[skuId] = (productId, serialized);

    public int LockCalls(Guid skuId) => _calls.GetValueOrDefault(skuId);

    public static async Task<FakeCatalog> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var catalog = new FakeCatalog();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(catalog);
        catalog._app = builder.Build();
        catalog._app.MapGrpcService<Service>();
        await catalog._app.StartAsync();
        catalog.Address = new Uri(catalog._app.Urls.First());
        return catalog;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private sealed class Service(FakeCatalog catalog) : CatalogInternal.CatalogInternalBase
    {
        public override Task<LockSkuForStockResponse> LockSkuForStock(LockSkuForStockRequest request, ServerCallContext context)
        {
            if (catalog.Down) throw new RpcException(new Status(StatusCode.Unavailable, "catalog is down"));
            if (!Guid.TryParse(request.SkuId, out var id) || !catalog._skus.TryGetValue(id, out var sku))
                throw BloomErrors.Create(StatusCode.NotFound, "NOT_FOUND", "SKU not found.");
            catalog._calls.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.FromResult(new LockSkuForStockResponse { ProductId = sku.ProductId.ToString(), Serialized = sku.Serialized });
        }
    }
}
