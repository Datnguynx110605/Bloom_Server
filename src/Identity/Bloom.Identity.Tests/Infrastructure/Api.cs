using System.Net.Http.Json;
using System.Text.Json;

namespace Bloom.Identity.Tests;

/// <summary>The identity the gateway would forward (LLD 07 §4.3), as the four <c>x-bloom-*</c> headers.</summary>
public sealed record TestCaller(Guid Id, string Type, string? Role, DateTimeOffset AuthTime)
{
    public static TestCaller Customer(Guid id, DateTimeOffset authTime) => new(id, "customer", null, authTime);

    public static TestCaller Staff(Guid id, string role, DateTimeOffset authTime) => new(id, "staff", role, authTime);

    internal void Apply(HttpRequestMessage request)
    {
        request.Headers.Add("x-bloom-user-id", Id.ToString("D"));
        request.Headers.Add("x-bloom-user-type", Type);
        if (Role is not null) request.Headers.Add("x-bloom-role", Role);
        request.Headers.Add("x-bloom-auth-time", AuthTime.ToUnixTimeSeconds().ToString());
    }
}

public static class Api
{
    /// <summary>Sends a request to the test host; <paramref name="body"/> is JSON with lowerCamel names.</summary>
    public static Task<HttpResponseMessage> SendAsync(
        this TestIdentity identity, HttpMethod method, string path, object? body = null, TestCaller? caller = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        caller?.Apply(request);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return identity.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Asserts the HTTP status and the <c>bloom</c> <c>ErrorInfo</c> reason, and returns the <c>ErrorInfo</c> element.</summary>
    public static async Task<JsonElement> ExpectErrorAsync(this HttpResponseMessage response, int httpStatus, string reason)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(httpStatus == (int)response.StatusCode, $"Expected HTTP {httpStatus} but got {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        var info = doc.RootElement.GetProperty("details").EnumerateArray()
            .Single(d => d.GetProperty("@type").GetString() == "type.googleapis.com/google.rpc.ErrorInfo").Clone();
        Assert.Equal("bloom", info.GetProperty("domain").GetString());
        Assert.Equal(reason, info.GetProperty("reason").GetString());
        return info;
    }

    /// <summary>The value of the <c>bloom_rt</c> cookie in <c>Set-Cookie</c>, or null when the response does not set it.</summary>
    public static string? RefreshCookie(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith("bloom_rt=", StringComparison.Ordinal)).Select(v => v["bloom_rt=".Length..].Split(';')[0]).FirstOrDefault()
            : null;
}
