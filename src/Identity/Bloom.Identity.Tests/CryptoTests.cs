using System.Security.Cryptography;
using System.Text;
using Bloom.Identity.Crypto;
using Bloom.Identity.Errors;
using Bloom.Identity.Validation;
using Grpc.Core;

namespace Bloom.Identity.Tests;

public class CryptoTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly Lazy<PasswordHasher> Hasher = new(() => new PasswordHasher());

    private static string Reason(RpcException e) => e.GetRpcStatus()!.Details.Single().Unpack<Google.Rpc.ErrorInfo>().Reason;

    private static string Pw(int scalars) => string.Concat(Enumerable.Range(0, scalars).Select(i => "xk7q"[i % 4]));

    [Fact]
    public void ID07_argon2id_parameters() =>
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", Hasher.Value.Hash("correct horse battery"));

    [Fact]
    public void Verify_and_rehash()
    {
        var hasher = Hasher.Value;
        var hash = hasher.Hash("correct horse battery");
        Assert.Equal((true, false), hasher.Verify(hash, "correct horse battery"));
        Assert.Equal((false, false), hasher.Verify(hash, "wrong horse battery"));

        var old = Sodium.PasswordHash.ArgonHashString("correct horse battery", 3, 33554432);
        Assert.Equal((true, true), hasher.Verify(old, "correct horse battery"));
    }

    [Fact]
    public void Null_hash_runs_one_dummy_verification()
    {
        var hasher = Hasher.Value;
        var before = hasher.Verifications;
        Assert.Equal((false, false), hasher.Verify(null, "whatever password"));
        Assert.Equal(before + 1, hasher.Verifications);

        hasher.VerifyDummy("whatever password");
        Assert.Equal(before + 2, hasher.Verifications);
    }

    [Theory, InlineData(59L, "287082"), InlineData(1111111109L, "081804"), InlineData(1234567890L, "005924")]
    public void Totp_matches_RFC6238_sha1_vectors(long unix, string code)
    {
        Assert.Equal(code, Totp.Compute(RfcSecret, unix / 30));
        Assert.True(Totp.Verify(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(unix), out var step));
        Assert.Equal(unix / 30, step);
    }

    [Fact]
    public void Totp_window_is_plus_minus_one_step()
    {
        const long n = 50_000_000;
        var code = Totp.Compute(RfcSecret, n);
        DateTimeOffset At(long step) => DateTimeOffset.FromUnixTimeSeconds(step * 30 + 5);

        foreach (var s in new[] { n - 1, n, n + 1 })
        {
            Assert.True(Totp.Verify(RfcSecret, code, At(s), out var matched));
            Assert.Equal(n, matched);
        }
        Assert.False(Totp.Verify(RfcSecret, code, At(n - 2), out _));
        Assert.False(Totp.Verify(RfcSecret, code, At(n + 2), out _));
        Assert.False(Totp.Verify(RfcSecret, "12345", At(n), out _));
        Assert.False(Totp.Verify(RfcSecret, "abcdef", At(n), out _));
    }

    [Fact]
    public void Base32_decodes_RFC4648()
    {
        const string text = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.True(Base32.TryDecode(text, out var bytes));
        Assert.Equal(RfcSecret, bytes);
        Assert.True(Base32.TryDecode(text.ToLowerInvariant(), out bytes));
        Assert.Equal(RfcSecret, bytes);
        Assert.True(Base32.TryDecode("MZXW6===", out bytes));
        Assert.Equal("foo"u8.ToArray(), bytes);
        Assert.True(Base32.TryDecode("MZXW6", out bytes));
        Assert.Equal("foo"u8.ToArray(), bytes);
        Assert.False(Base32.TryDecode("1", out _));
        Assert.False(Base32.TryDecode("MZXW6=A", out _));
        Assert.False(Base32.TryDecode("", out _));
    }

    [Fact]
    public void SecretBox_layout_and_tamper()
    {
        var box = new SecretBox(RandomNumberGenerator.GetBytes(32));
        var plain = "abcdef"u8.ToArray();
        var sealedBytes = box.Seal(plain);
        Assert.Equal(34, sealedBytes.Length);
        Assert.Equal(plain, box.Open(sealedBytes));
        Assert.NotEqual(sealedBytes, box.Seal(plain)); // fresh nonce

        sealedBytes[20] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => { box.Open(sealedBytes); });
        Assert.ThrowsAny<CryptographicException>(() => { box.Open(new byte[10]); });
    }

    [Fact]
    public void SecretBox_is_safe_under_concurrent_use()
    {
        var box = new SecretBox(RandomNumberGenerator.GetBytes(32));
        var failures = 0;
        Parallel.For(0, 500, i =>
        {
            var plain = Encoding.UTF8.GetBytes($"secret-{i}");
            if (!box.Open(box.Seal(plain)).AsSpan().SequenceEqual(plain)) Interlocked.Increment(ref failures);
        });
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Code_hash_depends_on_subject_purpose_and_code()
    {
        var hasher = new CodeHasher(RandomNumberGenerator.GetBytes(32));
        var hash = hasher.Hash("s", "signup", "123456");
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, hasher.Hash("s", "signup", "123456"));
        Assert.NotEqual(hash, hasher.Hash("t", "signup", "123456"));
        Assert.NotEqual(hash, hasher.Hash("s", "password_reset", "123456"));
        Assert.NotEqual(hash, hasher.Hash("s", "signup", "123457"));
        Assert.NotEqual(hash, new CodeHasher(RandomNumberGenerator.GetBytes(32)).Hash("s", "signup", "123456"));
    }

    [Fact]
    public void Opaque_tokens_are_random_and_hash_stably()
    {
        var a = Opaque.NewToken();
        Assert.Equal(43, a.Length); // 32 bytes, base64url, no padding
        Assert.DoesNotMatch("[+/=]", a);
        Assert.NotEqual(a, Opaque.NewToken());
        Assert.Equal(Opaque.Sha256(a), SHA256.HashData(Encoding.UTF8.GetBytes(a)));
    }

    [Theory, InlineData(11, false), InlineData(12, true), InlineData(128, true), InlineData(129, false)]
    public void Password_length_in_scalars(int n, bool ok)
    {
        var policy = new PasswordPolicy();
        if (ok) Assert.Equal(Pw(n), policy.Validate(Pw(n)));
        else Assert.Equal(IdentityErrors.PasswordWeak, Reason(Assert.Throws<RpcException>(() => policy.Validate(Pw(n)))));
    }

    [Fact]
    public void Emoji_passwords_count_scalars()
    {
        var policy = new PasswordPolicy();
        var twelve = string.Concat(Enumerable.Repeat("🌸", 12));
        Assert.Equal(twelve, policy.Validate(twelve));
        Assert.Equal(IdentityErrors.PasswordWeak, Reason(Assert.Throws<RpcException>(() => policy.Validate(string.Concat(Enumerable.Repeat("🌸", 11))))));
    }

    [Theory, InlineData("password1234"), InlineData("PASSWORD1234"), InlineData("ｐａｓｓｗｏｒｄ１２３４")]
    public void Breached_passwords_are_weak(string pw) =>
        Assert.Equal(IdentityErrors.PasswordWeak, Reason(Assert.Throws<RpcException>(() => new PasswordPolicy().Validate(pw))));

    [Fact]
    public void Password_normalizes_to_NFKC() =>
        Assert.Equal("Zq7-ｑ".Normalize(NormalizationForm.FormKC) + "xkxkxkxk", new PasswordPolicy().Validate("Zq7-ｑxkxkxkxk"));

    [Theory]
    [InlineData(" Ann@Example.COM ", "ann@example.com", "Ann@Example.COM")]
    [InlineData("bob@x.com", "bob@x.com", "bob@x.com")]
    public void Email_normalization(string raw, string normalized, string display) =>
        Assert.Equal((normalized, display), Inputs.Email(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b@x.com")]
    [InlineData("Ann <a@x.com>")]
    [InlineData("no-at-sign")]
    [InlineData("@x.com")]
    public void Email_rejects_malformed(string raw)
    {
        var e = Assert.Throws<RpcException>(() => Inputs.Email(raw));
        Assert.Equal(IdentityErrors.ValidationFailed, Reason(e));
        Assert.Equal("email", e.GetRpcStatus()!.Details.Single().Unpack<Google.Rpc.ErrorInfo>().Metadata["field"]);
    }

    [Fact]
    public void Email_length_limits()
    {
        Assert.Equal(IdentityErrors.ValidationFailed, Reason(Assert.Throws<RpcException>(() => Inputs.Email(new string('a', 65) + "@x.com"))));
        Assert.Equal(IdentityErrors.ValidationFailed, Reason(Assert.Throws<RpcException>(() => Inputs.Email(new string('a', 64) + "@" + new string('b', 190) + ".com"))));
        Inputs.Email(new string('a', 64) + "@x.com");
        Assert.Equal("owner_email", Assert.Throws<RpcException>(() => Inputs.Email("x", "owner_email")).GetRpcStatus()!.Details.Single().Unpack<Google.Rpc.ErrorInfo>().Metadata["field"]);
    }

    [Fact]
    public void Name_is_trimmed_and_counts_scalars()
    {
        Assert.Equal("Ann", Inputs.Name("  Ann "));
        Assert.Equal(100, Inputs.Name(string.Concat(Enumerable.Repeat("🌸", 100))).EnumerateRunes().Count());
        Assert.Equal(IdentityErrors.ValidationFailed, Reason(Assert.Throws<RpcException>(() => Inputs.Name("   "))));
        Assert.Equal(IdentityErrors.ValidationFailed, Reason(Assert.Throws<RpcException>(() => Inputs.Name(string.Concat(Enumerable.Repeat("🌸", 101))))));
    }

    [Fact]
    public void Address_field_validators()
    {
        Assert.Equal("1 Main St", Inputs.Text(" 1 Main St ", "line1", 1));
        Assert.Equal("", Inputs.Text("", "line2", 0));
        Assert.Throws<RpcException>(() => Inputs.Text("", "line1", 1));
        Assert.Throws<RpcException>(() => Inputs.Text(new string('a', 101), "city", 1));

        Assert.Equal("DC", Inputs.State("DC"));
        Assert.Equal("TX", Inputs.State("TX"));
        Assert.Throws<RpcException>(() => Inputs.State("ZZ"));
        Assert.Throws<RpcException>(() => Inputs.State(""));

        Assert.Equal("12345-6789", Inputs.Zip("12345-6789"));
        foreach (var bad in new[] { "1234", "12345-678", "12345\n", "１２３４５" })
            Assert.Throws<RpcException>(() => Inputs.Zip(bad));

        Assert.Null(Inputs.Phone(null));
        Assert.Null(Inputs.Phone(""));
        Assert.Equal("+14155550100", Inputs.Phone("+14155550100"));
        foreach (var bad in new[] { "4155550100", "+0123", "+1", "+1234567890123456" })
            Assert.Throws<RpcException>(() => Inputs.Phone(bad));
    }
}
