using System.Net;
using Bloom.Order.Contracts;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bloom.Identity.Tests;

/// <summary>The order service's <c>HasUnsettledOrders</c> on Kestrel <c>127.0.0.1:0</c>, HTTP/2 without TLS (plan I1).</summary>
public sealed class FakeOrder : IAsyncDisposable
{
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    /// <summary>What the next calls answer.</summary>
    public bool Unsettled { get; set; }

    /// <summary>Calls fail with <c>Unavailable</c>.</summary>
    public bool Fail { get; set; }

    /// <summary>Held before answering; the call ends early when its deadline passes.</summary>
    public TimeSpan Delay { get; set; }

    public Guid? LastAccountId { get; private set; }

    public static async Task<FakeOrder> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var order = new FakeOrder();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(order);
        order._app = builder.Build();
        order._app.MapGrpcService<Service>();
        await order._app.StartAsync();
        order.Address = new Uri(order._app.Urls.First());
        return order;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private sealed class Service(FakeOrder order) : OrderInternal.OrderInternalBase
    {
        public override async Task<HasUnsettledOrdersResponse> HasUnsettledOrders(HasUnsettledOrdersRequest request, ServerCallContext context)
        {
            order.LastAccountId = Guid.Parse(request.AccountId);
            if (order.Delay > TimeSpan.Zero) await Task.Delay(order.Delay, context.CancellationToken);
            if (order.Fail) throw new RpcException(new Status(StatusCode.Unavailable, "order is down"));
            return new HasUnsettledOrdersResponse { HasUnsettledOrders = order.Unsettled };
        }
    }
}
