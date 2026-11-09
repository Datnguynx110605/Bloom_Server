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

namespace Bloom.Launch.Tests;

/// <summary>
/// Catalog's <c>GetSku</c> on a Kestrel <c>127.0.0.1:0</c> host, HTTP/2 without TLS (as order's <c>FakeUpstreams</c>). A SKU never added answers <c>NOT_FOUND</c>.
/// </summary>
public sealed class FakeCatalog : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, GetSkuResponse> _skus = [];
    private readonly Lock _gate = new();
    private (StatusCode Code, int Left) _failure;
    private int _count;
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    /// <summary>Awaited at the start of every call, before it is counted (lets a test make a call slow).</summary>
    public Func<Task>? Before { get; set; }

    /// <summary>Calls received so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Adds an active SKU of an active product. <paramref name="launchId"/> is the launch catalog says it belongs to (null = none).</summary>
    public void Sku(Guid skuId, Guid productId, Guid? launchId = null) =>
        _skus[skuId.ToString("D")] = new GetSkuResponse { ProductId = productId.ToString("D"), Status = "active", Active = true, LaunchId = launchId?.ToString("D") ?? "" };

    /// <summary>The next <paramref name="times"/> calls fail with a plain <paramref name="code"/> status.</summary>
    public void Fail(StatusCode code, int times = 1)
    {
        lock (_gate) _failure = (code, times);
    }

    public static async Task<FakeCatalog> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var fake = new FakeCatalog();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(fake);
        fake._app = builder.Build();
        fake._app.MapGrpcService<Service>();
        await fake._app.StartAsync();
        fake.Address = new Uri(fake._app.Urls.First());
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private sealed class Service(FakeCatalog fake) : CatalogInternal.CatalogInternalBase
    {
        public override async Task<GetSkuResponse> GetSku(GetSkuRequest request, ServerCallContext context)
        {
            if (fake.Before is { } before) await before();
            Interlocked.Increment(ref fake._count);
            lock (fake._gate)
            {
                if (fake._failure.Left > 0)
                {
                    fake._failure = fake._failure with { Left = fake._failure.Left - 1 };
                    throw new RpcException(new Status(fake._failure.Code, "GetSku is scripted to fail."));
                }
            }
            return fake._skus.GetValueOrDefault(request.SkuId) ?? throw BloomErrors.Create(StatusCode.NotFound, "NOT_FOUND", "The SKU was not found.");
        }
    }
}
