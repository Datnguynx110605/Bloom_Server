using StackExchange.Redis;

namespace Bloom.Launch.WaitingRoom;

/// <summary>After a close commits (plan R16, spec §7): deletes the queue, the passes and the state cache of the launch. Best effort; the admission worker retries it.</summary>
public sealed class RoomCleanup(IConnectionMultiplexer redis, ILogger<RoomCleanup> logger)
{
    public async Task RunAsync(Guid launchId)
    {
        try
        {
            // One call: the three keys share the {launchId} hash tag, so they share a slot.
            await redis.GetDatabase().KeyDeleteAsync([RedisKeys.Queue(launchId), RedisKeys.Passes(launchId), RedisKeys.State(launchId)]);
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(e, "Could not clean up the waiting room of launch {LaunchId}; the admission worker retries.", launchId);
        }
    }
}
