using StackExchange.Redis;

namespace Bloom.Gateway.Tests;

public static class RedisStats
{
    /// <summary>One field (<c>calls</c>, <c>rejected_calls</c>, …) of <c>INFO commandstats</c> for <paramref name="command"/>; 0 if the command never ran.</summary>
    public static async Task<long> CommandStatAsync(IConnectionMultiplexer redis, string command, string field)
    {
        var stats = (await redis.GetServers().Single().InfoAsync("commandstats")).SelectMany(g => g);
        return stats.FirstOrDefault(p => p.Key == $"cmdstat_{command}").Value is { } line
            ? line.Split(',').Select(f => f.Split('=')).Where(f => f[0] == field).Select(f => long.Parse(f[1])).SingleOrDefault()
            : 0;
    }
}
