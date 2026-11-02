using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bloom.Catalog.Tests;

/// <summary>The identity the gateway would forward (LLD 07 §4.3), as the <c>x-bloom-*</c> headers.</summary>
public sealed record TestCaller(Guid Id, string Type, string? Role)
{
    public static TestCaller Customer(Guid id) => new(id, "customer", null);

    public static TestCaller Staff(string role, Guid? id = null) => new(id ?? Guid.NewGuid(), "staff", role);

    internal void Apply(HttpRequestMessage request)
    {
        request.Headers.Add("x-bloom-user-id", Id.ToString("D"));
        request.Headers.Add("x-bloom-user-type", Type);
        if (Role is not null) request.Headers.Add("x-bloom-role", Role);
    }
}

public static class Api
{
    /// <summary>Sends a request to the test host; <paramref name="body"/> is JSON with lowerCamel names.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        this TestCatalog catalog, HttpMethod method, string path, object? body = null, TestCaller? caller = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        caller?.Apply(request);
        return await catalog.Http.SendAsync(request, TestContext.Current.CancellationToken); // awaited, so the request outlives the send
    }

    /// <summary>Asserts HTTP 200 and returns the JSON body.</summary>
    public static async Task<JsonElement> OkJsonAsync(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
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
}
