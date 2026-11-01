using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bloom.Gateway.Tests;

/// <summary>Request and assertion helpers shared by the gateway test classes.</summary>
internal static class Http
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static HttpRequestMessage Request(string method, string path, string? token = null) =>
        new(new HttpMethod(method), path) { Headers = { Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token) } };

    public static Task<HttpResponseMessage> SendAsync(TestGateway gateway, string method, string path, string? token = null) =>
        gateway.Client.SendAsync(Request(method, path, token), Ct);

    /// <summary>Sends the request and returns the headers the upstream received, lower-cased.</summary>
    public static async Task<Dictionary<string, string>> EchoHeadersAsync(TestGateway gateway, HttpRequestMessage request)
    {
        using var response = await gateway.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    /// <summary>The response is a google.rpc.Status error with this HTTP status, code and a single ErrorInfo reason.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, int code, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetInt32());
        var detail = Assert.Single(body.RootElement.GetProperty("details").EnumerateArray());
        Assert.Equal(reason, detail.GetProperty("reason").GetString());
    }

    public static Task AssertOriginRejectedAsync(HttpResponseMessage response) =>
        AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ORIGIN_NOT_ALLOWED");
}
