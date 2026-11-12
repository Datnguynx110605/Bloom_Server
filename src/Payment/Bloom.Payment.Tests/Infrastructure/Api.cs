using Google.Rpc;
using Grpc.Core;

namespace Bloom.Payment.Tests;

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
