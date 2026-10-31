using Grpc.Core;

namespace Bloom.Shared.Tests;

/// <summary>Minimal <see cref="ServerCallContext"/>: a method name and request headers.</summary>
public sealed class FakeServerCallContext(string method, Metadata requestHeaders, CancellationToken ct = default) : ServerCallContext
{
    protected override string MethodCore => method;
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => requestHeaders;
    protected override CancellationToken CancellationTokenCore => ct;
    protected override Metadata ResponseTrailersCore { get; } = [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
