using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bloom.Launch.Passes;

namespace Bloom.Launch.Tests;

public class PassTokenTests
{
    private static readonly byte[] K0 = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] K1 = RandomNumberGenerator.GetBytes(32);

    private static PassTokens Tokens(string current, params string[] kids)
    {
        var options = new LaunchOptions
        {
            Mode = "api", Database = "d", RabbitMq = "r", Redis = "r", Catalog = "http://catalog:8080",
            CurrentPassKid = current,
            PassKeys = [.. kids.Select(k => new LaunchOptions.PassKey { Kid = k, Key = Convert.ToBase64String(k == "k0" ? K0 : K1) })],
        };
        options.Validate();
        return new PassTokens(options);
    }

    private static string Token(byte[] payload, byte[] key) =>
        Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));

    private static readonly Guid Pid = Guid.NewGuid(), Aid = Guid.NewGuid(), Lid = Guid.NewGuid();
    private const long Exp = 1_800_000_000_123;

    private static string Signed(PassTokens t) => t.Sign(Pid, Aid, Lid, Exp);

    [Fact]
    public void Signed_token_verifies_and_carries_its_claims()
    {
        var token = Signed(Tokens("k1", "k1", "k0"));

        Assert.Equal(new PassClaims(Pid, Aid, Lid, Exp, "k1"), Tokens("k1", "k1", "k0").Verify(token));
        Assert.Equal(2, token.Split('.').Length);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void Payload_is_the_documented_json()
    {
        var part = Signed(Tokens("k1", "k1", "k0")).Split('.')[0];

        using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(part));
        var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(["pid", "aid", "lid", "exp", "kid"], props.Keys);
        Assert.Equal(Pid.ToString("D"), props["pid"].GetString());
        Assert.Equal(Aid.ToString("D"), props["aid"].GetString());
        Assert.Equal(Lid.ToString("D"), props["lid"].GetString());
        Assert.Equal(JsonValueKind.Number, props["exp"].ValueKind);
        Assert.Equal(Exp, props["exp"].GetInt64());
        Assert.Equal("k1", props["kid"].GetString());
    }

    [Fact]
    public void Token_signed_with_an_old_key_still_verifies_after_rotation()
    {
        var token = Signed(Tokens("k0", "k0"));

        Assert.Equal("k0", Tokens("k1", "k0", "k1").Verify(token)?.Kid);
        Assert.Null(Tokens("k1", "k1").Verify(token)); // k0 no longer listed
    }

    [Fact]
    public void L16_tampered_token_is_rejected()
    {
        var tokens = Tokens("k1", "k1");
        var parts = Signed(tokens).Split('.');
        var other = tokens.Sign(Guid.NewGuid(), Aid, Lid, Exp).Split('.');

        var payload = Base64Url.DecodeFromChars(parts[0]);
        payload[10] ^= 1;
        var mac = Base64Url.DecodeFromChars(parts[1]);
        mac[0] ^= 1;

        Assert.Null(tokens.Verify(Base64Url.EncodeToString(payload) + "." + parts[1]));
        Assert.Null(tokens.Verify(parts[0] + "." + Base64Url.EncodeToString(mac)));
        Assert.Null(tokens.Verify(other[0] + "." + parts[1]));
    }

    public static TheoryData<string> Garbage()
    {
        string P(string json) => Token(Encoding.UTF8.GetBytes(json), K1);
        var (p, a, l) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return new(
            "", "abc", "a.b", "a.b.c", "!!.??",
            Token(Encoding.UTF8.GetBytes("not json"), K1),
            P($$"""{"pid":"{{p}}","aid":"{{a}}","lid":"{{l}}","kid":"k1"}"""),
            P($$"""{"pid":"{{p}}","aid":"{{a}}","lid":"{{l}}","exp":"123","kid":"k1"}"""),
            P($$"""{"pid":"x","aid":"{{a}}","lid":"{{l}}","exp":123,"kid":"k1"}"""),
            P($$"""{"pid":"{{p}}","aid":"{{a}}","lid":"{{l}}","exp":123,"kid":"nope"}"""),
            P($$"""{"pid":"{{p}}","aid":"{{a}}","lid":"{{l}}","exp":1.5,"kid":"k1"}"""),
            P($$"""{"pid":"{{p}}","aid":"{{a}}","lid":"{{l}}","exp":123,"kid":7}"""),
            P("[]"), P("null"), P("\"k1\""));
    }

    [Theory, MemberData(nameof(Garbage))]
    public void Garbage_is_rejected_without_throwing(string token) => Assert.Null(Tokens("k1", "k1").Verify(token));
}
