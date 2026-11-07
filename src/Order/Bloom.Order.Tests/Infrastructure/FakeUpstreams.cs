using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bloom.Catalog.Contracts;
using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Errors;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bloom.Order.Tests;

/// <summary>
/// The gRPC APIs order calls (catalog pricing, inventory stock, launch hold, payment creation, fulfillment cancel) on one Kestrel <c>127.0.0.1:0</c> host, HTTP/2 without TLS
/// (as fulfillment's <c>FakeInventory</c>). Every call succeeds unless scripted.
/// </summary>
public sealed class FakeUpstreams : IAsyncDisposable
{
    public sealed record Call(string Method, IMessage Request, string? UserId);

    private readonly ConcurrentQueue<Call> _calls = [];
    private readonly ConcurrentDictionary<string, SkuPricing> _skus = [];
    private readonly ConcurrentDictionary<string, int> _available = [];
    private readonly Dictionary<string, (StatusCode Code, string? Reason, int Left)> _failures = [];
    private TimeProvider _clock = null!;
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    /// <summary>Every call so far, oldest first. <c>UserId</c> is the forwarded <c>x-bloom-user-id</c>.</summary>
    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary>Awaited at the start of every call, with the method name, before it is recorded (lets a test make a call slow or look at the database meanwhile).</summary>
    public Func<string, Task>? Before { get; set; }

    /// <summary>Adds a SKU to the catalog. A SKU never added answers <c>buyable = false</c>.</summary>
    public void Sku(SkuPricing sku) => _skus[sku.SkuId] = sku;

    /// <summary>Sets the stock <c>GetAvailability</c> reports for the SKU (default 0).</summary>
    public void Available(Guid sku, int n) => _available[sku.ToString("D")] = n;

    /// <summary>The next <paramref name="times"/> calls of <paramref name="method"/> fail with <paramref name="code"/>: a <c>bloom</c> error with <paramref name="reason"/>, or a plain status when it is null.</summary>
    public void Fail(string method, StatusCode code, string? reason = null, int times = 1)
    {
        lock (_failures) _failures[method] = (code, reason, times);
    }

    public int Count(string method) => _calls.Count(c => c.Method == method);

    /// <summary>The payment id <c>CreatePayment</c> answers for the order; it is the same on every call.</summary>
    public static Guid PaymentIdOf(string orderId) => new(MD5.HashData(Encoding.UTF8.GetBytes(orderId)));

    public static async Task<FakeUpstreams> StartAsync(TimeProvider clock)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var fake = new FakeUpstreams { _clock = clock };
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(fake);
        fake._app = builder.Build();
        fake._app.MapGrpcService<Catalog>();
        fake._app.MapGrpcService<Stock>();
        fake._app.MapGrpcService<Launch>();
        fake._app.MapGrpcService<Payment>();
        fake._app.MapGrpcService<Fulfillment>();
        await fake._app.StartAsync();
        fake.Address = new Uri(fake._app.Urls.First());
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<T> HandleAsync<T>(string method, IMessage request, ServerCallContext context, Func<T> reply)
    {
        if (Before is { } before) await before(method);
        _calls.Enqueue(new Call(method, request, context.RequestHeaders.GetValue("x-bloom-user-id")));
        (StatusCode Code, string? Reason, int Left) f;
        lock (_failures)
        {
            if (_failures.TryGetValue(method, out f) && f.Left > 0) _failures[method] = f with { Left = f.Left - 1 };
            else f = default;
        }
        if (f.Left > 0)
            throw f.Reason is null
                ? new RpcException(new Status(f.Code, $"{method} is scripted to fail."))
                : BloomErrors.Create(f.Code, f.Reason, $"{method} is scripted to fail.");
        return reply();
    }

    private sealed class Catalog(FakeUpstreams fake) : CatalogInternal.CatalogInternalBase
    {
        public override Task<GetPricingResponse> GetPricing(GetPricingRequest request, ServerCallContext context) =>
            fake.HandleAsync("GetPricing", request, context, () => new GetPricingResponse
            {
                Items = { request.SkuIds.Distinct().Select(id => fake._skus.GetValueOrDefault(id) ?? new SkuPricing { SkuId = id, Buyable = false }) },
            });
    }

    private sealed class Stock(FakeUpstreams fake) : InventoryStock.InventoryStockBase
    {
        public override Task<Empty> ReserveStock(ReserveStockRequest request, ServerCallContext context) =>
            fake.HandleAsync("ReserveStock", request, context, () => new Empty());

        public override Task<GetAvailabilityResponse> GetAvailability(GetAvailabilityRequest request, ServerCallContext context) =>
            fake.HandleAsync("GetAvailability", request, context, () => new GetAvailabilityResponse
            {
                Items = { request.SkuIds.Distinct().Select(id => new SkuAvailability { SkuId = id, Available = fake._available.GetValueOrDefault(id) }) },
            });
    }

    private sealed class Launch(FakeUpstreams fake) : LaunchInternal.LaunchInternalBase
    {
        public override Task<HoldAllocationResponse> HoldAllocation(HoldAllocationRequest request, ServerCallContext context) =>
            fake.HandleAsync("HoldAllocation", request, context, () => new HoldAllocationResponse { HoldId = PaymentIdOf(request.OrderId).ToString("D"), Lines = { request.Lines } });
    }

    private sealed class Payment(FakeUpstreams fake) : PaymentInternal.PaymentInternalBase
    {
        public override Task<CreatePaymentResponse> CreatePayment(CreatePaymentRequest request, ServerCallContext context) =>
            fake.HandleAsync("CreatePayment", request, context, () => new CreatePaymentResponse
            {
                PaymentId = PaymentIdOf(request.OrderId).ToString("D"),
                CheckoutUrl = $"https://pay.test/{request.OrderId}",
                SessionExpiresAt = Timestamp.FromDateTimeOffset(fake._clock.GetUtcNow().AddMinutes(30)),
            });
    }

    private sealed class Fulfillment(FakeUpstreams fake) : FulfillmentInternal.FulfillmentInternalBase
    {
        public override Task<Empty> CancelPackTask(CancelPackTaskRequest request, ServerCallContext context) =>
            fake.HandleAsync("CancelPackTask", request, context, () => new Empty());
    }
}
