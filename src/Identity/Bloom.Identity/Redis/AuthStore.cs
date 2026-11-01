using Bloom.Identity.Errors;
using StackExchange.Redis;

namespace Bloom.Identity.Redis;

/// <summary>Identity's Redis counters (06-identity §4.2). Task 6 adds the sign-in and MFA members.</summary>
public sealed class AuthStore(IConnectionMultiplexer redis)
{
    private const int HourlyCap = 5;

    /// <summary>Resend cooldown, then the hourly cap, per (email, purpose) (I14). <paramref name="email"/> is the normalized address.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>CODE_RATE_LIMITED</c></exception>
    public async Task CheckCodeLimitsAsync(string email, string purpose)
    {
        var db = redis.GetDatabase();
        if (!await db.StringSetAsync($"code:cooldown:{email}:{purpose}", 1, TimeSpan.FromSeconds(60), When.NotExists))
            throw IdentityErrors.Error(IdentityErrors.CodeRateLimited, "Wait a minute before asking for another code.");

        var hourly = $"code:hourly:{email}:{purpose}";
        var count = await db.StringIncrementAsync(hourly);
        if (count == 1) await db.KeyExpireAsync(hourly, TimeSpan.FromHours(1));
        if (count > HourlyCap)
            throw IdentityErrors.Error(IdentityErrors.CodeRateLimited, "Too many codes requested. Try again later.");
    }
}
