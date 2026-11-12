using Google.Rpc;
using Grpc.Core;
using Bloom.Shared.Errors;

namespace Bloom.Shared.Tests;

public class ErrorTests
{
    [Fact]
    public void Create_carries_status_and_ErrorInfo()
    {
        var ex = BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, "stale");

        Assert.Equal(StatusCode.Aborted, ex.StatusCode);
        var info = ex.GetRpcStatus()!.GetDetail<ErrorInfo>();
        Assert.Equal("CONCURRENCY_CONFLICT", info.Reason);
        Assert.Equal("bloom", info.Domain);
    }

    [Fact]
    public void FailedPrecondition_uses_caller_reason()
    {
        var ex = BloomErrors.FailedPrecondition("SOLD_OUT", "none left");

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal("SOLD_OUT", ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason);
    }
}
