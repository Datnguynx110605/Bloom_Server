using Grpc.Core;

namespace Bloom.Fulfillment.Tests;

/// <summary>A <see cref="ServerCallContext"/> carrying only request headers, for calling domain classes that take the staff call (such as <c>InventoryCalls</c>) directly.</summary>
public sealed class FakeCallContext(TestCaller caller) : ServerCallContext
{
    private readonly Metadata _headers = new()
    {
        { "x-bloom-user-id", caller.Id.ToString("D") },
        { "x-bloom-user-type", caller.Type },
        { "x-bloom-role", caller.Role ?? "" },
    };

    protected override string MethodCore => "test";
    protected override string HostCore => "test";
    protected override string PeerCore => "test";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _headers;
    protected override CancellationToken CancellationTokenCore => TestContext.Current.CancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, []);
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
