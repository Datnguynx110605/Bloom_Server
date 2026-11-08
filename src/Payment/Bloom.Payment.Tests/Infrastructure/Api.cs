using Google.Rpc;
using Grpc.Core;

namespace Bloom.Payment.Tests;

/// <summary>The identity the gateway would forward (LLD 07 §4.3), as the <c>x-bloom-*</c> headers.</summary>
public sealed record TestCaller(Guid Id, string Type, string? Role)
{
    public static TestCaller Staff(string role, Guid? id = null) => new(id ?? Guid.NewGuid(), "staff", role);

    /// <summary>The same headers as gRPC call options, with the test's cancellation token.</summary>
    public CallOptions Call()
    {
        var headers = new Metadata { { "x-bloom-user-id", Id.ToString("D") }, { "x-bloom-user-type", Type } };
        if (Role is not null) headers.Add("x-bloom-role", Role);
        return new CallOptions(headers, cancellationToken: TestContext.Current.CancellationToken);
    }
}

public static class Rpc
{
    public static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single(d => d.Is(ErrorInfo.Descriptor)).Unpack<ErrorInfo>();

    /// <summary>Runs the call, which must fail with <paramref name="reason"/>; returns its <c>ErrorInfo</c>.</summary>
    public static async Task<ErrorInfo> FailsAsync(Task call, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call);
        var info = Info(e);
        Assert.Equal("bloom", info.Domain);
        Assert.Equal(reason, info.Reason);
        return info;
    }

    public static Task<ErrorInfo> FailsAsync<T>(AsyncUnaryCall<T> call, string reason) => FailsAsync(call.ResponseAsync, reason);
}
