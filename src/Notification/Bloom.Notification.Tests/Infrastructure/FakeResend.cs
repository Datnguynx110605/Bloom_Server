using System.Net;
using System.Text;
using System.Text.Json;

namespace Bloom.Notification.Tests;

/// <summary>One request the fake saw: <see cref="Path"/> without a leading slash, the raw headers, the parsed JSON body.</summary>
public sealed record ResendRequest(string Path, string? IdempotencyKey, string? Authorization, JsonElement Body);

/// <summary>
/// Resend's <c>POST emails</c> served from memory, behind the real <c>ResendEmailProvider</c> (plan N10; no test reaches the internet). Answers <c>{"id":"re_&lt;n&gt;"}</c>;
/// the same Idempotency-Key again gets the first ID and is not counted again in <see cref="Delivered"/>. Anything else answers 404 and is listed in <see cref="Unexpected"/>.
/// </summary>
public sealed class FakeResend : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<ResendRequest> _requests = [];
    private readonly List<string> _unexpected = [];
    private readonly Dictionary<string, string> _delivered = []; // idempotency key -> message id
    private readonly Queue<(int Status, string? Name)> _failures = [];
    private int _hangs, _count;

    public IReadOnlyList<ResendRequest> Requests { get { lock (_gate) return [.. _requests]; } }

    /// <summary>Requests that were not <c>POST emails</c> (each also answered 404). Tests may assert it is empty.</summary>
    public IReadOnlyList<string> Unexpected { get { lock (_gate) return [.. _unexpected]; } }

    /// <summary>The Idempotency-Keys of the emails actually delivered, once each; failed requests are not in it.</summary>
    public IReadOnlyCollection<string> Delivered { get { lock (_gate) return [.. _delivered.Keys]; } }

    /// <summary>Runs for every request after it is recorded and before it is served; failures and hangs come after.</summary>
    public Func<Task>? Before { get; set; }

    /// <summary>The next <paramref name="times"/> requests answer <paramref name="status"/>, with Resend's error body <c>{name, message, statusCode}</c> when <paramref name="name"/> is set.</summary>
    public void Fail(int status, string? name = null, int times = 1)
    {
        lock (_gate)
            for (var i = 0; i < times; i++) _failures.Enqueue((status, name));
    }

    /// <summary>The next request never answers: it waits until the caller cancels (or times out).</summary>
    public void HangNext()
    {
        lock (_gate) _hangs++;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var served = request.Method == HttpMethod.Post && path == "emails";
        JsonElement body = default;
        if (served) body = JsonDocument.Parse(text).RootElement;
        var key = request.Headers.TryGetValues("Idempotency-Key", out var v) ? v.Single() : null;
        lock (_gate)
        {
            if (served) _requests.Add(new(path, key, request.Headers.Authorization?.ToString(), body));
            else _unexpected.Add($"{request.Method} {path}");
        }
        if (!served) return Reply(HttpStatusCode.NotFound, new { name = "not_found", message = "fake Resend serves only POST emails", statusCode = 404 });
        if (Before is { } before) await before();

        bool hang, failed;
        (int Status, string? Name) failure = default;
        lock (_gate)
        {
            hang = _hangs > 0 && _hangs-- > 0;
            failed = !hang && _failures.TryDequeue(out failure);
        }
        if (hang)
        {
            await Task.Delay(Timeout.Infinite, ct); // ends with the caller's cancellation
        }
        if (failed)
            return Reply((HttpStatusCode)failure.Status, failure.Name is null ? new { message = "failed", statusCode = failure.Status } : new { name = failure.Name, message = "failed", statusCode = failure.Status } as object);

        lock (_gate)
        {
            if (key is null || !_delivered.TryGetValue(key, out var id))
            {
                id = $"re_{++_count}";
                if (key is not null) _delivered[key] = id;
            }
            return Reply(HttpStatusCode.OK, new { id });
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
