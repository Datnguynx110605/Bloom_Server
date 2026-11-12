using StackExchange.Redis;

namespace Bloom.Gateway.Tests;

#pragma warning disable CS0618 // the obsolete constructors are the only public ones that take no flags
public class RedisOutagePredicateTests
{
    private static RedisServerException Server(string message) => new(message);

    public static TheoryData<Exception, bool> Cases => new()
    {
        { new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"), true },
        { new RedisTimeoutException("slow", CommandStatus.Unknown), true },
        { new TimeoutException(), true },
        { Server("LOADING Redis is loading the dataset in memory"), true },
        { Server("READONLY You can't write against a read only replica."), true },
        { Server("MASTERDOWN Link with MASTER is down"), true },
        { Server("CLUSTERDOWN The cluster is down"), true },
        { Server("TRYAGAIN Multiple keys request during rehashing of slot"), true },
        { Server("BUSY Redis is busy running a script."), true },
        { Server("MISCONF Redis is configured to save RDB snapshots, but it's currently unable to persist to disk."), true },
        { Server("OOM command not allowed when used memory > 'maxmemory'."), true },
        { Server("NOREPLICAS Not enough good replicas to write."), true },
        { Server("NOPERM User probe has no permissions to run the 'ping' command"), false },
        { Server("WRONGTYPE Operation against a key holding the wrong kind of value"), false },
        { Server("ERR user_script:1: attempt to index a nil value"), false },
        { new InvalidOperationException(), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsRedisOutage_separates_unavailable_from_answered(Exception exception, bool expected) =>
        Assert.Equal(expected, EdgeMiddleware.IsRedisOutage(exception));
}
