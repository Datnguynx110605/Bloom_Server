using Microsoft.IdentityModel.Tokens;

namespace Bloom.Gateway;

/// <summary>
/// Identity's public signing keys (LLD 07 §6.1). Retried every second until the first load, then refreshed every 10 minutes;
/// an unknown <c>kid</c> triggers one refetch, at most once per 30 s across all kids (plan P14). All timing runs on <see cref="TimeProvider"/>.
/// </summary>
public sealed class JwksCache(IConfiguration configuration, TimeProvider time, GatewayMetrics metrics, ILogger<JwksCache> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1), RefreshDelay = TimeSpan.FromMinutes(10), UnknownKidInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _fetch = new(1, 1); // single-flight: one fetch at a time
    private DateTimeOffset _lastUnknownKidFetch = DateTimeOffset.MinValue;

    public bool IsLoaded { get; private set; }

    public IReadOnlyList<SecurityKey> Keys { get; private set; } = [];

    /// <returns>True when the <paramref name="kid"/> is known after the call.</returns>
    public async ValueTask<bool> RefreshForUnknownKidAsync(string kid)
    {
        await _fetch.WaitAsync();
        try
        {
            if (Keys.Any(k => k.KeyId == kid))
                return true; // another request already fetched it
            var now = time.GetUtcNow();
            if (now - _lastUnknownKidFetch < UnknownKidInterval)
                return false;
            _lastUnknownKidFetch = now;
            await FetchAsync(CancellationToken.None);
            return Keys.Any(k => k.KeyId == kid);
        }
        finally { _fetch.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _fetch.WaitAsync(stoppingToken);
            try { await FetchAsync(stoppingToken); }
            finally { _fetch.Release(); }
            try { await Task.Delay(IsLoaded ? RefreshDelay : RetryDelay, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(configuration["Gateway:JwksUrl"], ct);
            Keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            IsLoaded = true;
            metrics.JwksRefresh("success");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.JwksRefresh("error"); // the previous keys stay in use
            logger.LogWarning("JWKS fetch failed: {Error}", e.Message);
        }
    }
}
