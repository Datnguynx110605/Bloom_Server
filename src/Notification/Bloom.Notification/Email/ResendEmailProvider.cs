using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bloom.Notification.Email;

/// <summary>Resend over <c>HttpClient</c> and <c>System.Text.Json</c> (spec §7): one <c>POST emails</c> per send. Configure the client with <see cref="Configure"/>.</summary>
public sealed class ResendEmailProvider(HttpClient http, NotificationOptions options) : IEmailProvider
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Base address, bearer key and the 10 s timeout.</summary>
    public static void Configure(HttpClient http, NotificationOptions.ResendOptions resend)
    {
        http.BaseAddress = new Uri(resend.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resend.ApiKey);
    }

    public async Task<string?> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct)
    {
        // Messages never carry the address or body: they are PII and may hold a code.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(new { from = options.Email.From, to = new[] { message.To }, subject = message.Subject, html = message.Html, text = message.Text }),
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, body);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("id").GetString();
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) // HttpClient.Timeout; the caller's own cancellation propagates
        {
            throw new EmailSendException(true, "timeout", "Resend did not answer in time.", e);
        }
        catch (HttpRequestException e)
        {
            throw new EmailSendException(true, "network", "Resend could not be reached.", e);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A 2xx we cannot read: the mail may be sent, and the same Idempotency-Key makes a retry safe.
            throw new EmailSendException(true, "http_200", "Resend answered 200 without a readable id.", e);
        }
    }

    private static EmailSendException Failure(HttpStatusCode status, string body)
    {
        var message = $"Resend answered {(int)status}.";
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests) return new(true, $"http_{(int)status}", message);
        string? name = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
        }
        catch (JsonException) { } // no readable error object: fall back to the status
        return new(false, string.IsNullOrEmpty(name) ? $"http_{(int)status}" : name, message);
    }
}
