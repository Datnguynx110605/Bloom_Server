using System.Net.Http.Json;

namespace Bloom.Notification.Tests;

public sealed record MailpitMessage(string Subject, string Text, string Html);

/// <summary>Reads mail back from the Mailpit container over its HTTP API.</summary>
public static class Mailpit
{
    private sealed record Hits(List<Hit> Messages);

    private sealed record Hit(string Id);

    /// <summary>The newest message sent to <paramref name="to"/>, or null if Mailpit has none (yet).</summary>
    public static async Task<MailpitMessage?> FindAsync(Containers c, string to)
    {
        using var http = new HttpClient { BaseAddress = c.MailpitApi };
        var ct = TestContext.Current.CancellationToken;
        var hits = await http.GetFromJsonAsync<Hits>($"api/v1/search?query={Uri.EscapeDataString($"to:{to}")}", ct);
        if (hits?.Messages is not [var newest, ..]) return null; // newest first
        return await http.GetFromJsonAsync<MailpitMessage>($"api/v1/message/{newest.Id}", ct);
    }
}
