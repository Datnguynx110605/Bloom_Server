using Bloom.Order.Domain;
using Bloom.Shared.Idempotency;
using Grpc.Core;

namespace Bloom.Order.Tests;

public class UpstreamsTests
{
    [Fact]
    public void O3_is_rejection_matches_guard_stored_statuses()
    {
        foreach (var code in Enum.GetValues<StatusCode>())
            Assert.Equal(IdempotencyGuard.Stored.Contains(code), Upstreams.IsRejection(new RpcException(new Status(code, ""))));
    }
}
