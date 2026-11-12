using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Rpc;
using Grpc.Core;

namespace Bloom.Inventory.Tests;

/// <summary>The identity the gateway would forward (LLD 07 §4.3), as the <c>x-bloom-*</c> headers.</summary>
public sealed record TestCaller(Guid Id, string Type, string? Role)
{
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
        this TestInventory inventory, HttpMethod method, string path, object? body = null, TestCaller? caller = null, Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        caller?.Apply(request);
        if (idempotencyKey is { } key) request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await inventory.Http.SendAsync(request, TestContext.Current.CancellationToken); // awaited, so the request outlives the send
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

    /// <summary>Asserts the <c>VALIDATION_FAILED</c> error of the HTTP response names <paramref name="field"/>.</summary>
    public static async Task ExpectValidationAsync(this HttpResponseMessage response, string field)
    {
        var info = await response.ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal(field, info.GetProperty("metadata").GetProperty("field").GetString());
    }
}

public static class Rpc
{
    public static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single(d => d.Is(ErrorInfo.Descriptor)).Unpack<ErrorInfo>();

    /// <summary>Runs the call, which must fail with <paramref name="reason"/>; returns its <c>ErrorInfo</c>.</summary>
    public static async Task<ErrorInfo> FailsAsync(Task call, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call);
        var info = Info(e);
        Assert.Equal("bloom", info.Domain);
        Assert.Equal(reason, info.Reason);
        return info;
    }

    public static Task<ErrorInfo> FailsAsync<T>(AsyncUnaryCall<T> call, string reason) => FailsAsync(call.ResponseAsync, reason);
}
