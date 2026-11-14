using System.Diagnostics;
using System.Text.Json;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.Passes;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

using static Bloom.Launch.Tests.WaitingRooms;

namespace Bloom.Launch.Tests;

public class CustomerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync() => TestLaunch.StartAsync(containers);

    private static GetLaunchRequest Get(Guid launch) => new() { LaunchId = launch.ToString("D") };

    /// <summary>The cache is dropped as an admin write or the scheduler would after commit.</summary>
    private static Task DropCache(TestLaunch t, Guid launch) => t.Redis.KeyDeleteAsync(RedisKeys.State(launch));

    private static async Task<string> RoomState(TestLaunch t, Guid launch) =>
        (await t.Customer.GetLaunchAsync(Get(launch), cancellationToken: Ct).ResponseAsync).WaitingRoomState;

    [Fact]
    public async Task GetLaunch_returns_the_public_view_and_room_state()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(3, 10, 20), new SeedProduct(1, 5)]);

        var response = await t.Http.GetAsync($"/api/launches/{launch.Id}", TestContext.Current.CancellationToken); // no caller headers: public
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, json);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(launch.Id.ToString("D"), root.GetProperty("id").GetString());
        Assert.Equal("preorder_open", root.GetProperty("stage").GetString());
        Assert.Equal("open", root.GetProperty("waitingRoomState").GetString());
        Assert.True(root.TryGetProperty("launchTime", out _));
        Assert.True(root.TryGetProperty("releaseDate", out _));
        var products = root.GetProperty("products").EnumerateArray().ToDictionary(p => p.GetProperty("productId").GetString()!, p => p.GetProperty("purchaseLimit").GetInt32());
        Assert.Equal(new Dictionary<string, int> { [launch.Products[0].ToString("D")] = 3, [launch.Products[1].ToString("D")] = 1 }, products);
        var skus = root.GetProperty("skus").EnumerateArray().Select(s => (s.GetProperty("skuId").GetString(), s.GetProperty("productId").GetString())).ToHashSet();
        Assert.Equal(3, skus.Count);
        Assert.Contains((launch.Skus[2].ToString("D"), launch.Products[1].ToString("D")), skus);
        foreach (var counter in new[] { "allocation", "held", "sold", "version" })
            Assert.DoesNotContain($"\"{counter}", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Join_and_status_over_http()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var url = $"/api/launches/{launch.Id}/waiting-room";

        async Task<JsonElement> SendAsync(HttpMethod method)
        {
            using var request = new HttpRequestMessage(method, url).WithCaller(account, "customer");
            using var response = await t.Http.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        var joined = await SendAsync(HttpMethod.Post);
        Assert.Equal(("queued", 1), (joined.GetProperty("state").GetString(), joined.GetProperty("position").GetInt32()));
        Assert.InRange(joined.GetProperty("pollAfterSeconds").GetInt32(), 4, 6);

        await t.TickAsync(launch.Id);

        var status = await SendAsync(HttpMethod.Get);
        Assert.Equal("admitted", status.GetProperty("state").GetString());
        Assert.NotEmpty(status.GetProperty("passToken").GetString()!);
        Assert.Equal(JsonValueKind.String, status.GetProperty("passExpiresAt").ValueKind);

        using var anonymous = await t.Http.GetAsync(url, Ct); // no caller headers
        Assert.False(anonymous.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Draft_and_unknown_launches_are_NOT_FOUND()
    {
        await using var t = await StartAsync();
        var draft = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var customer = Api.Customer(Guid.NewGuid());

        foreach (var id in new[] { draft.Id.ToString("D"), Guid.NewGuid().ToString("D"), "x" })
        {
            var request = new WaitingRoomRequest { LaunchId = id };
            await Rpc.FailsAsync(t.Customer.GetLaunchAsync(new GetLaunchRequest { LaunchId = id }, cancellationToken: Ct), LaunchErrors.NotFoundReason);
            await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(request, customer, cancellationToken: Ct), LaunchErrors.NotFoundReason);
            await Rpc.FailsAsync(t.Customer.GetWaitingRoomStatusAsync(request, customer, cancellationToken: Ct), LaunchErrors.NotFoundReason);
        }
    }

    [Fact]
    public async Task Room_state_follows_the_clock_and_stage()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(20));

        Assert.Equal("not_open", await RoomState(t, launch.Id));
        t.Clock.Advance(TimeSpan.FromMinutes(6)); // 14 minutes to launch: the room opens 15 minutes before
        Assert.Equal("open", await RoomState(t, launch.Id));

        await t.SqlAsync($"UPDATE launches SET stage = 'preorder_open' WHERE id = {launch.Id}");
        await DropCache(t, launch.Id);
        Assert.Equal("open", await RoomState(t, launch.Id));

        await t.SqlAsync($"UPDATE launch_skus SET held = allocation WHERE launch_id = {launch.Id}");
        await DropCache(t, launch.Id);
        Assert.Equal("sold_out", await RoomState(t, launch.Id));

        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() WHERE id = {launch.Id}");
        await DropCache(t, launch.Id);
        Assert.Equal("closed", await RoomState(t, launch.Id)); // closed wins over sold_out

        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = NULL, stage = 'released' WHERE id = {launch.Id}");
        await DropCache(t, launch.Id);
        Assert.Equal("closed", await RoomState(t, launch.Id));
    }

    [Fact]
    public async Task Join_before_the_room_opens_is_WAITING_ROOM_NOT_OPEN()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(20));

        await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(Room(launch.Id), Api.Customer(Guid.NewGuid()), cancellationToken: Ct), LaunchErrors.WaitingRoomNotOpen);
        Assert.Equal(0, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
    }

    [Fact]
    public async Task Join_after_close_or_release_is_WAITING_ROOM_CLOSED()
    {
        await using var t = await StartAsync();
        var closed = await Seed.LaunchAsync(t);
        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() WHERE id = {closed.Id}");
        var released = await Seed.LaunchAsync(t, LaunchStages.Released);

        await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(Room(closed.Id), Api.Customer(Guid.NewGuid()), cancellationToken: Ct), LaunchErrors.WaitingRoomClosed);
        await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(Room(released.Id), Api.Customer(Guid.NewGuid()), cancellationToken: Ct), LaunchErrors.WaitingRoomClosed);
    }

    [Fact]
    public async Task Join_when_sold_out_returns_sold_out_and_does_not_queue()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(2, 5, 5)]);
        await t.SqlAsync($"UPDATE launch_skus SET held = 2, sold = allocation - 2 WHERE launch_id = {launch.Id}");

        var status = await Join(t, launch.Id, Guid.NewGuid());

        Assert.Equal("sold_out", status.State);
        Assert.InRange(status.PollAfterSeconds, 4, 6);
        Assert.Equal(0, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
    }

    [Fact]
    public async Task Join_before_launch_time_scores_in_0_1()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(10));
        var account = Guid.NewGuid();

        var status = await Join(t, launch.Id, account);

        Assert.Equal("queued", status.State);
        Assert.Equal(1, status.Position);
        var score = await t.Redis.SortedSetScoreAsync(RedisKeys.Queue(launch.Id), account.ToString("D"));
        Assert.InRange(score!.Value, 0, 0.999999999);
    }

    [Fact]
    public async Task L06_join_after_launch_time_queues_after_every_prelaunch_joiner_in_arrival_order()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(10));
        var early = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var account in early) await Join(t, launch.Id, account);

        t.Clock.Advance(TimeSpan.FromMinutes(10)); // now = launch_time
        var late = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var positions = new List<int>();
        foreach (var account in late)
        {
            t.Clock.Advance(TimeSpan.FromSeconds(1));
            positions.Add((await Join(t, launch.Id, account)).Position);
        }

        Assert.Equal([4, 5, 6], positions);
        var scores = new List<double>();
        foreach (var account in late) scores.Add((await t.Redis.SortedSetScoreAsync(RedisKeys.Queue(launch.Id), account.ToString("D")))!.Value);
        Assert.Equal([2d, 3d, 4d], scores); // 1 + elapsed seconds
        var earlyPositions = new List<int>();
        foreach (var account in early) earlyPositions.Add((await Status(t, launch.Id, account)).Position);
        Assert.Equal([1, 2, 3], earlyPositions.Order());
    }

    [Fact]
    public async Task Account_joins_twice_and_keeps_its_first_score()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        var first = await Join(t, launch.Id, account);
        var score = await t.Redis.SortedSetScoreAsync(RedisKeys.Queue(launch.Id), account.ToString("D"));
        t.Clock.Advance(TimeSpan.FromSeconds(5));
        await Join(t, launch.Id, other);
        var second = await Join(t, launch.Id, account);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(1, second.Position);
        Assert.Equal(score, await t.Redis.SortedSetScoreAsync(RedisKeys.Queue(launch.Id), account.ToString("D")));
        Assert.Equal(2, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
    }

    [Fact]
    public async Task Join_with_a_pass_returns_admitted()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var passId = await Seed.PassAsync(t, launch.Id, account);

        var status = await Join(t, launch.Id, account);

        Assert.Equal("admitted", status.State);
        var claims = t.Services.GetRequiredService<PassTokens>().Verify(status.PassToken);
        Assert.NotNull(claims);
        Assert.Equal((passId, account, launch.Id), (claims.PassId, claims.AccountId, claims.LaunchId));
        Assert.Equal(0, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
    }

    [Fact]
    public async Task Status_queued_shows_the_position()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Join(t, launch.Id, first);
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        await Join(t, launch.Id, second);
        using var metrics = new MetricCapture(t.Services);

        var status = await Status(t, launch.Id, second);

        Assert.Equal(1, metrics.Count("waiting_room_status_seconds"));
        Assert.Equal("queued", status.State);
        Assert.Equal(2, status.Position);
        Assert.InRange(status.PollAfterSeconds, 4, 6);
        Assert.Equal("", status.PassToken);
        Assert.Null(status.PassExpiresAt);
    }

    [Fact]
    public async Task Status_admitted_shows_token_and_expiry()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        await Seed.PassAsync(t, launch.Id, account);
        var stored = (await t.Redis.StringGetAsync(RedisKeys.Pass(launch.Id, account))).ToString().Split('|');

        var status = await Status(t, launch.Id, account);

        Assert.Equal("admitted", status.State);
        Assert.Equal(long.Parse(stored[1]), status.PassExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds());
        var claims = t.Services.GetRequiredService<PassTokens>().Verify(status.PassToken);
        Assert.NotNull(claims);
        Assert.Equal((Guid.Parse(stored[0]), long.Parse(stored[1])), (claims.PassId, claims.ExpiresAtUnixMs));
        Assert.Equal(0, status.Position);
        Assert.Equal(0, status.PollAfterSeconds);
    }

    [Fact]
    public async Task Status_when_not_queued_in_an_open_room_is_NOT_IN_QUEUE()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);

        await Rpc.FailsAsync(t.Customer.GetWaitingRoomStatusAsync(Room(launch.Id), Api.Customer(Guid.NewGuid()), cancellationToken: Ct), LaunchErrors.NotInQueue);
    }

    [Fact]
    public async Task Status_when_not_queued_before_the_room_opens_is_not_open_with_polling()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(20));

        var status = await Status(t, launch.Id, Guid.NewGuid());

        Assert.Equal("not_open", status.State);
        Assert.InRange(status.PollAfterSeconds, 4, 6);
        Assert.Equal(0, status.Position);
    }

    [Fact]
    public async Task Pass_expires_unused_then_status_is_NOT_IN_QUEUE_and_rejoin_goes_to_the_back()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        await Seed.PassAsync(t, launch.Id, account);
        foreach (var other in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            t.Clock.Advance(TimeSpan.FromSeconds(1));
            await Join(t, launch.Id, other);
        }
        Assert.Equal("admitted", (await Status(t, launch.Id, account)).State);

        await t.Redis.KeyDeleteAsync(RedisKeys.Pass(launch.Id, account)); // the TTL ran out

        await Rpc.FailsAsync(t.Customer.GetWaitingRoomStatusAsync(Room(launch.Id), Api.Customer(account), cancellationToken: Ct), LaunchErrors.NotInQueue);
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        var rejoined = await Join(t, launch.Id, account);
        Assert.Equal("queued", rejoined.State);
        Assert.Equal(3, rejoined.Position);
    }

    [Fact]
    public async Task Closed_room_shows_closed_to_a_queued_customer()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        Assert.Equal("queued", (await Join(t, launch.Id, account)).State);

        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() WHERE id = {launch.Id}"); // the queue is still there
        await DropCache(t, launch.Id);

        var status = await Status(t, launch.Id, account);
        Assert.Equal("closed", status.State);
        Assert.Equal(0, status.PollAfterSeconds);
    }

    [Fact]
    public async Task Live_pass_still_shows_admitted_after_close()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        await Seed.PassAsync(t, launch.Id, account);
        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() WHERE id = {launch.Id}");
        await DropCache(t, launch.Id);

        Assert.Equal("admitted", (await Status(t, launch.Id, account)).State);
    }

    [Fact]
    public async Task Status_never_queries_postgres()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var queued = Guid.NewGuid();
        var admitted = Guid.NewGuid();
        var absent = Guid.NewGuid();
        await Join(t, launch.Id, queued);
        await Seed.PassAsync(t, launch.Id, admitted);
        Assert.Equal("queued", (await Status(t, launch.Id, queued)).State); // the cache is warm
        var before = t.Commands.Count;

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal("queued", (await Status(t, launch.Id, queued)).State);
            Assert.Equal("admitted", (await Status(t, launch.Id, admitted)).State);
            await Rpc.FailsAsync(t.Customer.GetWaitingRoomStatusAsync(Room(launch.Id), Api.Customer(absent), cancellationToken: Ct), LaunchErrors.NotInQueue);
        }

        Assert.Equal(before, t.Commands.Count);
    }

    [Fact]
    public async Task Cache_miss_loads_once_per_launch()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var before = t.Commands.Count;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => t.Customer.GetLaunchAsync(Get(launch.Id), cancellationToken: Ct).ResponseAsync));

        Assert.Equal(1, t.Commands.Count - before);
    }

    [Fact]
    public async Task Unknown_launch_is_cached_as_missing()
    {
        await using var t = await StartAsync();
        var id = Guid.NewGuid();
        var before = t.Commands.Count;

        await Rpc.FailsAsync(t.Customer.GetLaunchAsync(Get(id), cancellationToken: Ct), LaunchErrors.NotFoundReason);
        await Rpc.FailsAsync(t.Customer.GetLaunchAsync(Get(id), cancellationToken: Ct), LaunchErrors.NotFoundReason);

        Assert.Equal(1, t.Commands.Count - before);
    }

    [Fact]
    public async Task Staff_cannot_join_or_read_status()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);

        await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(Room(launch.Id), Api.Staff("admin"), cancellationToken: Ct), BloomErrors.RoleNotAllowed);
        await Rpc.FailsAsync(t.Customer.GetWaitingRoomStatusAsync(Room(launch.Id), Api.Staff("admin"), cancellationToken: Ct), BloomErrors.RoleNotAllowed);
    }

    [Fact]
    public async Task Redis_down_is_UNAVAILABLE()
    {
        await using var t = await TestLaunch.StartAsync(containers, o => o.Config["Launch:Redis"] = "127.0.0.1:1,connectTimeout=500");
        var launch = await Seed.LaunchAsync(t);
        var customer = Api.Customer(Guid.NewGuid());
        var before = t.Commands.Count;

        var calls = new Func<Task>[]
        {
            () => t.Customer.GetLaunchAsync(Get(launch.Id), cancellationToken: Ct).ResponseAsync,
            () => t.Customer.JoinWaitingRoomAsync(Room(launch.Id), customer, cancellationToken: Ct).ResponseAsync,
            () => t.Customer.GetWaitingRoomStatusAsync(Room(launch.Id), customer, cancellationToken: Ct).ResponseAsync,
        };
        foreach (var call in calls)
        {
            var clock = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<RpcException>(call);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
            Assert.Equal(StatusCode.Unavailable, e.StatusCode);
        }

        Assert.Equal(before, t.Commands.Count); // never falls back to Postgres (LR-04)
        var warnings = t.Logs.Entries.Where(e => e.StartsWith("[Warning]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.DoesNotContain("wr:", w)); // no Redis key, no stack trace
        Assert.All(warnings, w => Assert.DoesNotContain("   at ", w));
    }
}
