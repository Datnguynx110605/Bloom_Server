using Bloom.Identity.Crypto;
using Bloom.Identity.Errors;
using StackExchange.Redis;

namespace Bloom.Identity.Redis;

/// <summary>Identity's Redis counters (06-identity §4.2). Code limits, sign-in and MFA lockout, MFA challenges and the TOTP replay block.</summary>
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

        var count = (long)await db.ScriptEvaluateAsync(CountScript, [$"code:hourly:{email}:{purpose}"], [3600]);
        if (count > HourlyCap)
            throw IdentityErrors.Error(IdentityErrors.CodeRateLimited, "Too many codes requested. Try again later.");
    }

    // INCR and the first EXPIRE in one call: a crash between them would leave a counter that never expires.
    private const string CountScript = @"
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        return n";

    // Failure counter and block (I12): the 5th failure sets the block and drops the counter, atomically.
    private const string FailureScript = @"
        local n = redis.call('INCR', KEYS[1])
        if n == 1 then redis.call('EXPIRE', KEYS[1], 900) end
        if n >= 5 then
            redis.call('SET', KEYS[2], 1, 'EX', 900)
            redis.call('DEL', KEYS[1])
        end";

    // Reads the account, then counts the attempt; the 5th attempt deletes the challenge (I13). A missing key is never recreated.
    private const string UseChallengeScript = @"
        local account = redis.call('HGET', KEYS[1], 'accountId')
        if not account then return false end
        if redis.call('HINCRBY', KEYS[1], 'attempts', 1) >= 5 then redis.call('DEL', KEYS[1]) end
        return account";

    public Task<bool> IsSignInBlockedAsync(Guid id) => redis.GetDatabase().KeyExistsAsync($"signin:block:{id}");

    public Task RecordSignInFailureAsync(Guid id) => RecordFailureAsync("signin", id);

    public Task ClearSignInFailuresAsync(Guid id) => redis.GetDatabase().KeyDeleteAsync($"signin:fail:{id}");

    public Task<bool> IsMfaBlockedAsync(Guid id) => redis.GetDatabase().KeyExistsAsync($"mfa:block:{id}");

    public Task RecordMfaFailureAsync(Guid id) => RecordFailureAsync("mfa", id);

    public Task ClearMfaFailuresAsync(Guid id) => redis.GetDatabase().KeyDeleteAsync($"mfa:fail:{id}");

    private Task RecordFailureAsync(string prefix, Guid id) =>
        redis.GetDatabase().ScriptEvaluateAsync(FailureScript, [$"{prefix}:fail:{id}", $"{prefix}:block:{id}"]);

    /// <summary>Opens a staff MFA challenge for 5 minutes and returns its opaque id (I13).</summary>
    public async Task<string> CreateChallengeAsync(Guid accountId)
    {
        var id = Opaque.NewToken();
        var key = $"mfa:challenge:{id}";
        var tx = redis.GetDatabase().CreateTransaction();
        _ = tx.HashSetAsync(key, [new HashEntry("accountId", accountId.ToString()), new HashEntry("attempts", 0)]);
        _ = tx.KeyExpireAsync(key, TimeSpan.FromMinutes(5));
        if (!await tx.ExecuteAsync()) throw new InvalidOperationException("Could not store the MFA challenge.");
        return id;
    }

    /// <summary>The challenge's account, counting this attempt; null when the challenge is missing or used up.</summary>
    public async Task<Guid?> TryUseChallengeAsync(string id)
    {
        var result = await redis.GetDatabase().ScriptEvaluateAsync(UseChallengeScript, [$"mfa:challenge:{id}"]);
        return result.IsNull ? null : Guid.Parse((string)result!);
    }

    public Task DeleteChallengeAsync(string id) => redis.GetDatabase().KeyDeleteAsync($"mfa:challenge:{id}");

    /// <summary>True the first time a TOTP step is used for the account, so a code can't be replayed in its window (spec §7).</summary>
    public Task<bool> TryMarkTotpUsedAsync(Guid accountId, long step) =>
        redis.GetDatabase().StringSetAsync($"mfa:used:{accountId}:{step}", 1, TimeSpan.FromSeconds(90), When.NotExists);
}
