using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bloom.Launch.Passes;

public sealed record PassClaims(Guid PassId, Guid AccountId, Guid LaunchId, long ExpiresAtUnixMs, string Kid);

/// <summary>Admission pass tokens (01-launch §4.3): <c>base64url(payload) "." base64url(HMAC-SHA256(key[kid], payload))</c>.</summary>
public sealed class PassTokens(LaunchOptions options)
{
    public string Sign(Guid passId, Guid accountId, Guid launchId, long expiresAtUnixMs)
    {
        var kid = options.CurrentPassKid;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            pid = passId.ToString("D"), aid = accountId.ToString("D"), lid = launchId.ToString("D"), exp = expiresAtUnixMs, kid,
        });
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(options.DecodedKeys[kid], payload));
    }

    /// <summary>The claims, or null for anything that is not a token signed with a listed key. Never throws; never checks <c>exp</c> (the hold does, against the database clock, §6.5).</summary>
    public PassClaims? Verify(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2 || !Base64Url.IsValid(parts[0]) || !Base64Url.IsValid(parts[1])) return null;
        var payload = Base64Url.DecodeFromChars(parts[0]);
        var mac = Base64Url.DecodeFromChars(parts[1]);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kid", out var kidEl) || kidEl.ValueKind != JsonValueKind.String
                || !options.DecodedKeys.TryGetValue(kidEl.GetString()!, out var key)) return null;
            // The MAC comes first: no other field is trusted before it holds.
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), mac)) return null;
            if (!Id(root, "pid", out var pid) || !Id(root, "aid", out var aid) || !Id(root, "lid", out var lid)
                || !root.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expMs)) return null;
            return new PassClaims(pid, aid, lid, expMs, kidEl.GetString()!);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null; // not JSON, or a string with invalid UTF-8 (GetString throws InvalidOperationException)
        }
    }

    private static bool Id(JsonElement root, string name, out Guid id)
    {
        id = default;
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.TryGetGuid(out id);
    }
}
