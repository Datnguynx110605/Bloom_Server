using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Bloom.Notification.Tests;

/// <summary>09-notification §5.1 customer API over transcoded HTTP (spec §6): own rows only, customers only.</summary>
public class ApiTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(status == response.StatusCode, $"Expected HTTP {(int)status} but got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ErrorInfoAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var body = await JsonAsync(response, status);
        var info = body.GetProperty("details").EnumerateArray()
            .Single(d => d.GetProperty("@type").GetString() == "type.googleapis.com/google.rpc.ErrorInfo").Clone();
        Assert.Equal("bloom", info.GetProperty("domain").GetString());
        Assert.Equal(reason, info.GetProperty("reason").GetString());
        return info;
    }

    /// <summary>The documented token: created_at as int64 microseconds since the epoch, big-endian, then the 16 id bytes, base64url.</summary>
    private static string Token(DateTimeOffset createdAt, Guid id)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt64BigEndian(bytes, (createdAt - DateTimeOffset.UnixEpoch).Ticks / 10);
        id.TryWriteBytes(bytes.AsSpan(8), bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string? token = null) =>
        await JsonAsync(await client.GetAsync(token is null ? "/api/notifications" : $"/api/notifications?page_token={token}", Ct));

    private static string[] Ids(JsonElement page) => page.GetProperty("notifications").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToArray();

    private static async Task<DateTimeOffset?> ReadAtAsync(TestNotification t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Notifications.AsNoTracking().Where(n => n.Id == id).Select(n => n.ReadAt).SingleAsync(Ct);
    }

    [Fact]
    public async Task List_is_newest_first_20_per_page()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        var start = t.Clock.GetUtcNow().AddMinutes(-10);
        start = new DateTimeOffset(start.Ticks - start.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var rows = new List<(Guid Id, DateTimeOffset At)>();
        // 40 distinct seconds (0..40 but 24) plus 5 rows on second 24: the tie straddles the first page break
        foreach (var s in Enumerable.Range(0, 41).Where(s => s != 24).Concat(Enumerable.Repeat(24, 5)))
            rows.Add((await Seed.NotificationAsync(t, account, start.AddSeconds(s)), start.AddSeconds(s)));
        var expected = rows.OrderByDescending(r => r.At).ThenByDescending(r => r.Id.ToByteArray(bigEndian: true), Comparer<byte[]>.Create(Bytes)).Select(r => r.Id.ToString("D")).ToArray();
        using var client = t.CustomerClient(account);

        var page1 = await ListAsync(client);
        var page2 = await ListAsync(client, page1.GetProperty("nextPageToken").GetString());
        var page3 = await ListAsync(client, page2.GetProperty("nextPageToken").GetString());

        Assert.Equal([20, 20, 5], new[] { page1, page2, page3 }.Select(p => Ids(p).Length));
        Assert.Equal(expected, Ids(page1).Concat(Ids(page2)).Concat(Ids(page3)));
        Assert.NotEqual("", page1.GetProperty("nextPageToken").GetString());
        Assert.Equal("", page3.GetProperty("nextPageToken").GetString());
    }

    private static int Bytes(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    [Fact]
    public async Task List_returns_the_documented_fields()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        var at = t.Clock.GetUtcNow();
        var unread = await Seed.NotificationAsync(t, account, at.AddMinutes(-1), "order.shipped");
        var read = await Seed.NotificationAsync(t, account, at.AddMinutes(-2), "order.confirmed", readAt: at);
        using var client = t.CustomerClient(account);

        var page = await ListAsync(client);

        var items = page.GetProperty("notifications").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["id", "type", "title", "body", "link", "read", "createdAt"], items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(unread.ToString("D"), items[0].GetProperty("id").GetString());
        Assert.Equal("order.shipped", items[0].GetProperty("type").GetString());
        Assert.Equal($"Title {unread:N}", items[0].GetProperty("title").GetString());
        Assert.Equal($"Body {unread:N}", items[0].GetProperty("body").GetString());
        Assert.Equal($"/orders/{unread:N}", items[0].GetProperty("link").GetString());
        Assert.False(items[0].GetProperty("read").GetBoolean());
        Assert.True(Math.Abs((items[0].GetProperty("createdAt").GetDateTimeOffset() - at.AddMinutes(-1)).TotalMilliseconds) < 1);
        Assert.Equal(read.ToString("D"), items[1].GetProperty("id").GetString());
        Assert.True(items[1].GetProperty("read").GetBoolean());
        Assert.Equal("", page.GetProperty("nextPageToken").GetString());
    }

    [Fact]
    public async Task List_shows_only_the_callers_rows() // Review Focus 4
    {
        await using var t = await TestNotification.StartAsync(containers);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var start = t.Clock.GetUtcNow().AddHours(-1);
        var a1 = await Seed.NotificationAsync(t, a, start.AddSeconds(1));
        var b2 = await Seed.NotificationAsync(t, b, start.AddSeconds(2));
        var a3 = await Seed.NotificationAsync(t, a, start.AddSeconds(3));
        var b4 = await Seed.NotificationAsync(t, b, start.AddSeconds(4));
        var a5 = await Seed.NotificationAsync(t, a, start.AddSeconds(5));
        using var client = t.CustomerClient(a);

        var all = Ids(await ListAsync(client));
        var fromBsRow = Ids(await ListAsync(client, Token(start.AddSeconds(4), b4)));

        Assert.Equal([a5, a3, a1], all.Select(Guid.Parse));
        Assert.Equal([a3, a1], fromBsRow.Select(Guid.Parse));
        Assert.DoesNotContain(b2.ToString("D"), all.Concat(fromBsRow));
        Assert.DoesNotContain(b4.ToString("D"), all.Concat(fromBsRow));
    }

    public static TheoryData<string> BadTokens() => new() { "x!", Base64Url.EncodeToString(new byte[3]), Base64Url.EncodeToString(new byte[25]), "x" };

    [Theory, MemberData(nameof(BadTokens))]
    public async Task Malformed_page_token_is_VALIDATION_FAILED(string token)
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var client = t.CustomerClient(Guid.NewGuid());

        var info = await ErrorInfoAsync(await client.GetAsync($"/api/notifications?page_token={Uri.EscapeDataString(token)}", Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        Assert.Equal("page_token", info.GetProperty("metadata").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Unread_count_counts_unread_rows_of_the_caller()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var at = t.Clock.GetUtcNow();
        await Seed.NotificationAsync(t, a, at);
        await Seed.NotificationAsync(t, a, at);
        await Seed.NotificationAsync(t, a, at, readAt: at);
        await Seed.NotificationAsync(t, b, at);
        using var client = t.CustomerClient(a);

        var body = await JsonAsync(await client.GetAsync("/api/notifications/unread-count", Ct));

        Assert.Equal(2, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Mark_read_marks_only_the_callers_unread_rows()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var earlier = t.Clock.GetUtcNow().AddHours(-2);
        var own = await Seed.NotificationAsync(t, a, earlier);
        var alreadyRead = await Seed.NotificationAsync(t, a, earlier, readAt: earlier);
        var others = await Seed.NotificationAsync(t, b, earlier);
        t.Clock.Advance(TimeSpan.FromMinutes(5));
        using var client = t.CustomerClient(a);

        var response = await client.PostAsJsonAsync("/api/notifications/read", new { ids = new[] { own, alreadyRead, others, Guid.NewGuid() } }, Ct);

        Assert.Equal("{}", (await JsonAsync(response)).GetRawText());
        Assert.Equal(t.Clock.GetUtcNow(), await ReadAtAsync(t, own));
        Assert.Equal(earlier, await ReadAtAsync(t, alreadyRead));
        Assert.Null(await ReadAtAsync(t, others));
    }

    public static TheoryData<string> BadIds() => new()
    {
        """{"ids":[]}""",
        "{}",
        $$"""{"ids":[{{string.Join(',', Enumerable.Range(0, 101).Select(_ => $"\"{Guid.NewGuid()}\""))}}]}""",
        $$"""{"ids":["{{Guid.NewGuid()}}","x"]}""",
        """{"ids":["x"]}""",
    };

    [Theory, MemberData(nameof(BadIds))]
    public async Task Mark_read_validates_ids(string json)
    {
        await using var t = await TestNotification.StartAsync(containers);
        var a = Guid.NewGuid();
        var own = await Seed.NotificationAsync(t, a, t.Clock.GetUtcNow());
        using var client = t.CustomerClient(a);

        var info = await ErrorInfoAsync(await client.PostAsync("/api/notifications/read", new StringContent(json, Encoding.UTF8, "application/json"), Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        Assert.Equal("ids", info.GetProperty("metadata").GetProperty("field").GetString());
        Assert.Null(await ReadAtAsync(t, own));
    }

    [Fact]
    public async Task Mark_all_read_marks_every_unread_row_of_the_caller()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var earlier = t.Clock.GetUtcNow().AddHours(-2);
        var unread1 = await Seed.NotificationAsync(t, a, earlier);
        var unread2 = await Seed.NotificationAsync(t, a, earlier);
        var alreadyRead = await Seed.NotificationAsync(t, a, earlier, readAt: earlier);
        var others = await Seed.NotificationAsync(t, b, earlier);
        t.Clock.Advance(TimeSpan.FromMinutes(5));
        using var client = t.CustomerClient(a);

        var response = await client.PostAsJsonAsync("/api/notifications/read-all", new { }, Ct);

        Assert.Equal("{}", (await JsonAsync(response)).GetRawText());
        Assert.Equal(t.Clock.GetUtcNow(), await ReadAtAsync(t, unread1));
        Assert.Equal(t.Clock.GetUtcNow(), await ReadAtAsync(t, unread2));
        Assert.Equal(earlier, await ReadAtAsync(t, alreadyRead));
        Assert.Null(await ReadAtAsync(t, others));
    }

    [Fact]
    public async Task Ws_ticket_is_stored_for_30_seconds()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        using var client = t.CustomerClient(account);
        var redis = t.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

        var first = (await JsonAsync(await client.GetAsync("/api/notifications/ws-ticket", Ct))).GetProperty("ticket").GetString()!;
        var second = (await JsonAsync(await client.GetAsync("/api/notifications/ws-ticket", Ct))).GetProperty("ticket").GetString()!;

        Assert.NotEqual(first, second);
        foreach (var ticket in new[] { first, second })
        {
            Assert.Equal(43, ticket.Length);
            Assert.Equal(32, Base64Url.DecodeFromChars(ticket).Length);
            Assert.Equal(account.ToString("D"), (string?)await redis.StringGetAsync($"ws:ticket:{ticket}"));
            var ttl = await redis.KeyTimeToLiveAsync($"ws:ticket:{ticket}");
            Assert.InRange(ttl!.Value.TotalSeconds, 0.001, 30);
        }
    }

    public static TheoryData<string, string, string?> Calls() => new()
    {
        { "GET", "/api/notifications", null },
        { "GET", "/api/notifications/unread-count", null },
        { "POST", "/api/notifications/read", """{"ids":["x"]}""" }, // the role check comes before validation
        { "POST", "/api/notifications/read-all", "{}" },
        { "GET", "/api/notifications/ws-ticket", null },
    };

    [Theory, MemberData(nameof(Calls))]
    public async Task Staff_callers_are_ROLE_NOT_ALLOWED(string method, string path, string? body)
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var client = t.StaffClient("support");
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json") };

        await ErrorInfoAsync(await client.SendAsync(request, Ct), HttpStatusCode.Forbidden, "ROLE_NOT_ALLOWED");
    }

    [Theory, MemberData(nameof(Calls))]
    public async Task Missing_caller_headers_are_a_server_error(string method, string path, string? body)
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json") };

        var response = await t.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
