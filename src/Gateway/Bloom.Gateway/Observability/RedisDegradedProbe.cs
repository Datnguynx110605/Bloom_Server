using StackExchange.Redis;

namespace Bloom.Gateway;

/// <summary>Pings Redis on a timer, so <c>gateway_redis_degraded</c> also moves while no request arrives (LLD 07 §10).</summary>
public sealed class RedisDegradedProbe(IConnectionMultiplexer redis, GatewayMetrics metrics, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
                await redis.GetDatabase().PingAsync();
                metrics.SetRedisDegraded(false);
            }
            catch (Exception e) when (e is RedisException or TimeoutException) // same outage test as EdgeMiddleware
            {
                metrics.SetRedisDegraded(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
