using Bloom.Launch.Contracts;
using Google.Rpc;
using Grpc.Core;

namespace Bloom.Launch.Tests;

/// <summary>The identity the gateway would forward (LLD 07 §4.3), as the <c>x-bloom-*</c> headers.</summary>
public static class Api
{
    public static Metadata Customer(Guid accountId) => new() { { "x-bloom-user-id", accountId.ToString("D") }, { "x-bloom-user-type", "customer" } };

    public static Metadata Staff(string role = "catalog_manager") =>
        new() { { "x-bloom-user-id", Guid.NewGuid().ToString("D") }, { "x-bloom-user-type", "staff" }, { "x-bloom-role", role } };

    /// <summary>Adds the same headers to an HTTP request for the transcoded routes.</summary>
    public static HttpRequestMessage WithCaller(this HttpRequestMessage request, Guid id, string type, string? role = null)
    {
        request.Headers.Add("x-bloom-user-id", id.ToString("D"));
        request.Headers.Add("x-bloom-user-type", type);
        if (role is not null) request.Headers.Add("x-bloom-role", role);
        return request;
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

/// <summary>The customer waiting-room calls, as the caller account.</summary>
public static class WaitingRooms
{
    public static WaitingRoomRequest Room(Guid launch) => new() { LaunchId = launch.ToString() };

    public static Task<WaitingRoomStatus> Join(TestLaunch t, Guid launch, Guid account) =>
        t.Customer.JoinWaitingRoomAsync(Room(launch), Api.Customer(account), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    public static Task<WaitingRoomStatus> Status(TestLaunch t, Guid launch, Guid account) =>
        t.Customer.GetWaitingRoomStatusAsync(Room(launch), Api.Customer(account), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
}
