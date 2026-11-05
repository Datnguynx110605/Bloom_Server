using System.Collections.Concurrent;
using System.Net;
using Bloom.Inventory.Contracts;
using Bloom.Shared.Errors;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bloom.Fulfillment.Tests;

/// <summary>Inventory's packing and stock APIs on Kestrel <c>127.0.0.1:0</c>, HTTP/2 without TLS (as inventory's <c>FakeCatalog</c>). Every call succeeds unless scripted.</summary>
public sealed class FakeInventory : IAsyncDisposable
{
    public sealed record Call(string Method, IMessage Request, string? UserId);

    private readonly ConcurrentQueue<Call> _calls = [];
    private readonly ConcurrentDictionary<string, (StatusCode Code, string Reason, int Left)> _failures = [];
    private WebApplication _app = null!;

    public Uri Address { get; private set; } = null!;

    /// <summary>Every call so far, oldest first. <c>UserId</c> is the forwarded <c>x-bloom-user-id</c>.</summary>
    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary><c>CommitForReship</c> answers <c>SOLD_OUT</c>.</summary>
    public bool SoldOut { get; set; }

    /// <summary>Awaited at the start of every call, before it is recorded (lets a test make a call slow or look at the database meanwhile).</summary>
    public Func<Task>? Before { get; set; }

    /// <summary>The next <paramref name="times"/> calls of <paramref name="method"/> (for example <c>AssignSerial</c>) fail with a <c>bloom</c> error.</summary>
    public void Fail(string method, StatusCode code, string reason, int times = 1) => _failures[method] = (code, reason, times);

    public int Count(string method) => _calls.Count(c => c.Method == method);

    public static async Task<FakeInventory> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var inventory = new FakeInventory();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(inventory);
        inventory._app = builder.Build();
        inventory._app.MapGrpcService<Packing>();
        inventory._app.MapGrpcService<Stock>();
        await inventory._app.StartAsync();
        inventory.Address = new Uri(inventory._app.Urls.First());
        return inventory;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<Empty> HandleAsync(string method, IMessage request, ServerCallContext context)
    {
        if (Before is { } before) await before();
        _calls.Enqueue(new Call(method, request, context.RequestHeaders.GetValue("x-bloom-user-id")));
        if (_failures.TryGetValue(method, out var f) && f.Left > 0)
        {
            _failures[method] = f with { Left = f.Left - 1 };
            throw BloomErrors.Create(f.Code, f.Reason, $"{method} is scripted to fail.");
        }
        if (SoldOut && method == "CommitForReship") throw BloomErrors.Create(StatusCode.FailedPrecondition, "SOLD_OUT", "Sold out.");
        return new Empty();
    }

    private sealed class Packing(FakeInventory fake) : InventoryPacking.InventoryPackingBase
    {
        public override Task<Empty> AssignSerial(AssignSerialRequest request, ServerCallContext context) => fake.HandleAsync("AssignSerial", request, context);

        public override Task<Empty> UnassignSerial(UnassignSerialRequest request, ServerCallContext context) => fake.HandleAsync("UnassignSerial", request, context);

        public override Task<Empty> ReplaceSerial(ReplaceSerialRequest request, ServerCallContext context) => fake.HandleAsync("ReplaceSerial", request, context);
    }

    private sealed class Stock(FakeInventory fake) : InventoryStock.InventoryStockBase
    {
        public override Task<Empty> CommitForReship(CommitForReshipRequest request, ServerCallContext context) => fake.HandleAsync("CommitForReship", request, context);
    }
}
