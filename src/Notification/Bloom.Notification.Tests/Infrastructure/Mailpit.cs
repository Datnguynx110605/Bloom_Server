using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bloom.Notification.Tests;

public sealed record MailpitMessage(string Subject, string Text, string Html);

/// <summary>Reads mail back from the Mailpit container over its HTTP API.</summary>
public static class Mailpit
{
    private sealed record Hits([property: JsonPropertyName("messages")] List<Hit> Messages);

    private sealed record Hit([property: JsonPropertyName("ID")] string Id);

    private sealed record Detail(
        [property: JsonPropertyName("Subject")] string Subject, [property: JsonPropertyName("Text")] string Text, [property: JsonPropertyName("HTML")] string Html);

    /// <summary>The newest message sent to <paramref name="to"/>, or null if Mailpit has none (yet).</summary>
    public static async Task<MailpitMessage?> FindAsync(Containers c, string to)
    {
        using var http = new HttpClient { BaseAddress = c.MailpitApi };
        var ct = TestContext.Current.CancellationToken;
        var hits = await http.GetFromJsonAsync<Hits>($"api/v1/search?query={Uri.EscapeDataString($"to:{to}")}", ct);
        if (hits?.Messages is not [var newest, ..]) return null; // newest first
        var detail = await http.GetFromJsonAsync<Detail>($"api/v1/message/{newest.Id}", ct);
        return new(detail!.Subject, detail.Text, detail.Html);
    }
}
