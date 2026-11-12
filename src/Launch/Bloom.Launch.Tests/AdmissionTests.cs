using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.WaitingRoom;
using Bloom.Launch.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

using static Bloom.Launch.Tests.WaitingRooms;

namespace Bloom.Launch.Tests;

public class AdmissionTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync() => TestLaunch.StartAsync(containers);

    /// <summary>Queues <paramref name="n"/> accounts straight in Redis with the scores <paramref name="scores"/> (default 1, 2, …).</summary>
    private static async Task<List<Guid>> QueueAsync(TestLaunch t, Guid launch, int n, Func<int, double>? scores = null)
    {
        var accounts = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        await t.Redis.SortedSetAddAsync(RedisKeys.Queue(launch), [.. accounts.Select((a, i) => new SortedSetEntry(a.ToString("D"), (scores ?? (x => x + 1))(i)))]);
        return accounts;
    }

    private static async Task<long> StageEventsAsync(TestLaunch t, string stage, Guid? launch = null) =>
        (await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged)).Count(e => e.Stage == stage && (launch is null || e.LaunchId == launch));

    [Fact]
    public async Task Tick_admits_up_to_the_rate_lowest_scores_first()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t, rate: 3);
        var accounts = await QueueAsync(t, launch.Id, 5, i => new[] { 5.0, 3, 1, 4, 2 }[i]);
        await using var db = t.NewDb();
        var before = await db.NowAsync(Ct);

        var result = await t.TickAsync(launch.Id);

        Assert.Equal("admitted", result.Result);
        Assert.Equal([accounts[2], accounts[4], accounts[1]], result.Admitted);
        Assert.Equal(2, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
        Assert.Equal(3, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
        foreach (var account in result.Admitted)
        {
            var key = RedisKeys.Pass(launch.Id, account);
            var parts = ((string)(await t.Redis.StringGetAsync(key))!).Split('|');
            var exp = long.Parse(parts[1]);
            Assert.InRange(exp, before.AddSeconds(600).ToUnixTimeMilliseconds(), (await db.NowAsync(Ct)).AddSeconds(600).ToUnixTimeMilliseconds());
            Assert.Equal(exp, await t.Redis.SortedSetScoreAsync(RedisKeys.Passes(launch.Id), parts[0]));
            Assert.InRange((await t.Redis.KeyTimeToLiveAsync(key))!.Value.TotalSeconds, 590, 600);
        }
        Assert.Equal(3, metrics.Sum("admissions_total", ("launch", launch.Id.ToString("D"))));
        Assert.Equal(1, metrics.Sum("admission_ticks_total", ("launch", launch.Id.ToString("D")), ("result", "admitted")));
        Assert.Equal(2, metrics.Latest("waiting_room_size", ("launch", launch.Id.ToString("D"))));
        Assert.Equal(3, metrics.Latest("passes_outstanding", ("launch", launch.Id.ToString("D"))));
    }

    [Fact]
    public async Task Admitted_customer_sees_admitted_and_can_hold()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 10));
        var account = Guid.NewGuid();
        Assert.Equal("queued", (await Join(t, launch.Id, account)).State);

        await t.RunAdmissionAsync();

        var status = await Status(t, launch.Id, account);
        Assert.Equal("admitted", status.State);
        Assert.NotEmpty(status.PassToken);
        var hold = await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), status.PassToken, (launch.Skus[0], 1)));
        Assert.NotEmpty(hold.HoldId);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Empty_queue_admits_nobody()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t);

        var result = await t.TickAsync(launch.Id);

        Assert.Equal("none", result.Result);
        Assert.Empty(result.Admitted);
        Assert.Equal(0, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
        Assert.Equal(1, metrics.Sum("admission_ticks_total", ("launch", launch.Id.ToString("D")), ("result", "none")));
    }

    [Fact]
    public async Task L05_prelaunch_joiners_are_admitted_in_random_order()
    {
        await using var t = await StartAsync();
        const int n = 10_000;
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, launchIn: TimeSpan.FromMinutes(10), rate: 200, products: new SeedProduct(2, n));
        var accounts = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        foreach (var account in accounts) Assert.Equal("queued", (await Join(t, launch.Id, account)).State);

        await t.SqlAsync($"UPDATE launches SET launch_time = now() - interval '1 minute', stage = 'preorder_open' WHERE id = {launch.Id}");
        var admitted = new List<Guid>();
        for (var tick = 0; tick < 50; tick++) admitted.AddRange((await t.TickAsync(launch.Id)).Admitted);

        Assert.Equal(n, admitted.Count);
        Assert.Equal(n, admitted.Distinct().Count());
        var joined = accounts.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => (double)x.i);
        var rho = Spearman(admitted.Select(a => joined[a]).ToArray());
        Assert.True(Math.Abs(rho) < 0.05, $"Spearman rho {rho}");
        Assert.NotEqual(accounts, admitted);
    }

    /// <summary>Spearman's rho between the admission order (0, 1, …) and the given join positions, both permutations of 0 … n−1.</summary>
    private static double Spearman(double[] joinPositionByAdmission)
    {
        var n = joinPositionByAdmission.Length;
        var d2 = joinPositionByAdmission.Select((join, admission) => (join - admission) * (join - admission)).Sum();
        return 1 - 6 * d2 / ((double)n * ((double)n * n - 1));
    }

    [Fact]
    public async Task L07_two_workers_tick_together()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        const int queued = 300;
        var launch = await Seed.LaunchAsync(t, rate: 5, products: new SeedProduct(2, 1000));
        var accounts = await QueueAsync(t, launch.Id, queued);

        await using (var conn = new NpgsqlConnection(t.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var tx = await conn.BeginTransactionAsync(Ct);
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('launch-admission:' || @l))", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("l", launch.Id.ToString("D"));
                await lockCmd.ExecuteNonQueryAsync(Ct);
            }
            var skipped = await t.TickAsync(launch.Id);
            Assert.Equal("skipped_lock", skipped.Result);
            Assert.Empty(skipped.Admitted);
            Assert.Equal(queued, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
            await tx.RollbackAsync(Ct);
        }
        Assert.Equal(1, metrics.Sum("admission_ticks_total", ("launch", launch.Id.ToString("D")), ("result", "skipped_lock")));
        Assert.Equal(5, (await t.TickAsync(launch.Id)).Admitted.Count);

        var admittedInRounds = 0;
        for (var round = 0; round < 20; round++)
        {
            var results = await Task.WhenAll(t.TickAsync(launch.Id), t.TickAsync(launch.Id));
            var admitted = results.Sum(r => r.Admitted.Count);
            Assert.True(admitted <= 10, $"round {round} admitted {admitted}"); // at most the rate for each of the two ticks
            admittedInRounds += admitted;
            Assert.Equal(5 + admittedInRounds, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id))); // every admitted account has its passes entry
        }

        // Nobody was popped without a pass key, and nobody was admitted twice.
        var inQueue = 0;
        var withPass = 0;
        foreach (var account in accounts)
        {
            var queuedStill = (await t.Redis.SortedSetScoreAsync(RedisKeys.Queue(launch.Id), account.ToString("D"))) is not null;
            var hasPass = await t.Redis.KeyExistsAsync(RedisKeys.Pass(launch.Id, account));
            Assert.True(queuedStill ^ hasPass, $"Account {account}: queued {queuedStill}, pass {hasPass}");
            if (queuedStill) inQueue++; else withPass++;
        }
        Assert.Equal(withPass, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
        Assert.Equal(5 + admittedInRounds, withPass);
        Assert.Equal(queued, inQueue + withPass);
    }

    [Fact]
    public async Task Redis_failure_at_pop()
    {
        // A Redis user that may do everything but ZPOPMIN: the pacing key is set and the pop itself fails inside the script.
        var user = $"nopop-{Guid.NewGuid():N}";
        await containers.Redis.GetDatabase().ExecuteAsync("ACL", "SETUSER", user, "on", "nopass", "~*", "&*", "+@all", "-zpopmin");
        try
        {
            await using var t = await TestLaunch.StartAsync(containers, o => o.Config["Launch:Redis"] = $"{containers.RedisConnectionString},user={user},password=x");
            var redis = t.Services.GetRequiredService<IConnectionMultiplexer>();
            await Poll.UntilAsync(() => Task.FromResult(redis.IsConnected), 10, "the app's Redis connection");
            var launch = await Seed.LaunchAsync(t, rate: 3);
            await QueueAsync(t, launch.Id, 5);

            await Assert.ThrowsAnyAsync<RedisException>(() => t.TickAsync(launch.Id));

            Assert.Equal(5, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
            Assert.Equal(0, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
            Assert.Equal(0, (await t.RowAsync(launch.Id)).Version);
            Assert.Equal(0, t.ErrorLogs); // the tick throws to its caller; the worker's loop is what logs

            await t.Redis.KeyDeleteAsync(RedisKeys.Tick(launch.Id));
            await t.RunAdmissionAsync();

            Assert.Equal(1, t.ErrorLogs);
            Assert.Equal(5, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
        }
        finally
        {
            await containers.Redis.GetDatabase().ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task L07_two_workers_500ms_apart_admit_at_most_rate_per_second()
    {
        await using var t = await StartAsync();
        const int rate = 5;
        var launch = await Seed.LaunchAsync(t, rate: rate, products: new SeedProduct(2, 1000));
        await QueueAsync(t, launch.Id, 500);

        // Redis expires the tick key on its own clock; the test models the 1000 ms on the fake timeline (explicit tick instants, one tick per 500 ms for 10 s, two workers taking turns).
        var admitted = 0;
        var keyExpiresAt = TimeSpan.Zero;
        for (var at = TimeSpan.Zero; at < TimeSpan.FromSeconds(10); at += TimeSpan.FromMilliseconds(500))
        {
            if (at >= keyExpiresAt) await t.Redis.KeyDeleteAsync(RedisKeys.Tick(launch.Id));
            var existed = await t.Redis.KeyExistsAsync(RedisKeys.Tick(launch.Id));
            admitted += (await t.TickPacedAsync(launch.Id)).Admitted.Count;
            if (!existed) keyExpiresAt = at + TimeSpan.FromSeconds(1);
            t.Clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        // One admission per model second. The key's real 1 s TTL runs in parallel: a test stall of more than 1 s between two ticks can (rarely) add an extra admit.
        Assert.Equal(10 * rate, admitted);
    }

    [Fact]
    public async Task L07_two_workers_same_instant_one_pops()
    {
        // Review Focus 4: the advisory lock only stops overlapping ticks; the second worker ticks right after the first one committed.
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, rate: 5, products: new SeedProduct(2, 1000));
        await QueueAsync(t, launch.Id, 20);
        await t.Redis.KeyDeleteAsync(RedisKeys.Tick(launch.Id));

        var first = await t.TickPacedAsync(launch.Id);
        var second = await t.TickPacedAsync(launch.Id);

        Assert.Equal(5, first.Admitted.Count);
        Assert.Empty(second.Admitted);
        Assert.Equal("none", second.Result);
        Assert.Equal(15, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
        Assert.Equal(5, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
    }

    [Fact]
    public async Task L07_two_workers_at_once_pop_at_most_once()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, rate: 5, products: new SeedProduct(2, 1000));
        await QueueAsync(t, launch.Id, 20);
        await t.Redis.KeyDeleteAsync(RedisKeys.Tick(launch.Id));

        var results = await Task.WhenAll(t.TickPacedAsync(launch.Id), t.TickPacedAsync(launch.Id));

        Assert.Equal(5, results.Sum(r => r.Admitted.Count)); // the loser is "skipped_lock" or "none", by timing
        Assert.Equal(15, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
    }

    [Fact]
    public async Task L07_tick_key_expires_after_1000ms()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, rate: 5, products: new SeedProduct(2, 1000));
        await QueueAsync(t, launch.Id, 20);
        await t.Redis.KeyDeleteAsync(RedisKeys.Tick(launch.Id));

        Assert.Equal(5, (await t.TickPacedAsync(launch.Id)).Admitted.Count);

        // The key the script set, not one the test touched: it has a TTL of at most 1 s, so the next second's tick is not stalled forever.
        var ttl = await t.Redis.KeyTimeToLiveAsync(RedisKeys.Tick(launch.Id));
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.Zero.Add(TimeSpan.FromTicks(1)), TimeSpan.FromMilliseconds(1000));
        await Poll.UntilAsync(async () => !await t.Redis.KeyExistsAsync(RedisKeys.Tick(launch.Id)), 5, "the tick key expires");
        Assert.Equal(5, (await t.TickPacedAsync(launch.Id)).Admitted.Count);
    }

    [Fact]
    public async Task L08_50_available_and_50_outstanding_admits_0()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 50));
        for (var i = 0; i < 50; i++) await Seed.PassAsync(t, launch.Id, Guid.NewGuid());
        await QueueAsync(t, launch.Id, 5);

        var result = await t.TickAsync(launch.Id);

        Assert.Equal("none", result.Result);
        Assert.Equal(5, await t.Redis.SortedSetLengthAsync(RedisKeys.Queue(launch.Id)));
        Assert.Equal(50, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
        Assert.Equal(50, metrics.Latest("passes_outstanding", ("launch", launch.Id.ToString("D"))));
    }

    [Fact]
    public async Task Expired_passes_free_their_slots()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 1));
        var expired = Guid.NewGuid().ToString("D");
        await t.Redis.SortedSetAddAsync(RedisKeys.Passes(launch.Id), expired, DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeMilliseconds());
        var accounts = await QueueAsync(t, launch.Id, 1);

        var result = await t.TickAsync(launch.Id);

        Assert.Equal(accounts, result.Admitted);
        Assert.Null(await t.Redis.SortedSetScoreAsync(RedisKeys.Passes(launch.Id), expired));
        Assert.Equal(1, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(launch.Id)));
    }

    [Fact]
    public async Task L12_sold_out_and_no_holds_left_closes_the_room()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 3));
        var queued = Guid.NewGuid();
        Assert.Equal("queued", (await Join(t, launch.Id, queued)).State);
        Assert.True(await t.Redis.KeyExistsAsync(RedisKeys.State(launch.Id)));
        await Seed.PassAsync(t, launch.Id, Guid.NewGuid());
        await t.SqlAsync($"UPDATE launch_skus SET sold = allocation WHERE launch_id = {launch.Id}");

        var result = await t.TickAsync(launch.Id);

        Assert.Equal("none", result.Result);
        var row = await t.RowAsync(launch.Id);
        Assert.NotNull(row.WaitingRoomClosedAt);
        Assert.Equal(1, row.Version);
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Queue(launch.Id)));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Passes(launch.Id)));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.State(launch.Id)));
        Assert.Equal(0, metrics.Latest("waiting_room_size", ("launch", launch.Id.ToString())));
        Assert.Equal(0, metrics.Latest("passes_outstanding", ("launch", launch.Id.ToString())));
        Assert.Equal("closed", (await Status(t, launch.Id, queued)).State);
        await Rpc.FailsAsync(t.Customer.JoinWaitingRoomAsync(Room(launch.Id), Api.Customer(Guid.NewGuid()), cancellationToken: Ct), LaunchErrors.WaitingRoomClosed);
    }

    [Fact]
    public async Task Sold_out_with_holds_left_keeps_the_room_open()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, rate: 1, products: new SeedProduct(2, 2));
        var holder = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        await Join(t, launch.Id, holder);
        await t.Redis.SortedSetAddAsync(RedisKeys.Queue(launch.Id), waiting.ToString("D"), 1e9); // behind the holder at any rate
        await t.TickAsync(launch.Id);
        var token = (await Status(t, launch.Id, holder)).PassToken;
        await Holds.CallAsync(t, holder, Holds.Request(Guid.NewGuid(), token, (launch.Skus[0], 2)));

        var result = await t.TickAsync(launch.Id);

        Assert.Equal("none", result.Result);
        Assert.Null((await t.RowAsync(launch.Id)).WaitingRoomClosedAt);
        Assert.True(await t.Redis.KeyExistsAsync(RedisKeys.Queue(launch.Id)));
        await t.Redis.KeyDeleteAsync(RedisKeys.State(launch.Id)); // the cached state may predate the hold
        Assert.Equal("sold_out", (await Status(t, launch.Id, Guid.NewGuid())).State);
        Assert.Equal("queued", (await Status(t, launch.Id, waiting)).State);
    }

    [Fact]
    public async Task Close_statement_rechecks_stock_and_holds_itself()
    {
        // The tick reads "available = 0" in one statement and closes in another; the close must not trust the earlier read (a release may commit in between).
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 3));
        async Task<bool> TryClose()
        {
            await using var db = t.NewDb();
            return await AdmissionWorker.TryCloseAsync(db, launch.Id, Ct);
        }

        // Units came back (available 3): no close.
        Assert.False(await TryClose());
        Assert.Null((await t.RowAsync(launch.Id)).WaitingRoomClosedAt);

        // Sold out but a hold is still held: no close.
        await t.SqlAsync($"UPDATE launch_skus SET held = 3 WHERE launch_id = {launch.Id}");
        await t.SqlAsync($"INSERT INTO holds (id, order_id, launch_id, account_id, pass_id, status, version, created_at, updated_at) VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {launch.Id}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'held', 0, now(), now())");
        Assert.False(await TryClose());

        // The hold is released and its units return: still no close.
        await t.SqlAsync($"UPDATE holds SET status = 'released' WHERE launch_id = {launch.Id}");
        await t.SqlAsync($"UPDATE launch_skus SET held = 0 WHERE launch_id = {launch.Id}");
        Assert.False(await TryClose());
        Assert.Null((await t.RowAsync(launch.Id)).WaitingRoomClosedAt);

        // Sold out and no held hold: closes once, and only once.
        await t.SqlAsync($"UPDATE launch_skus SET sold = allocation WHERE launch_id = {launch.Id}");
        Assert.True(await TryClose());
        Assert.False(await TryClose());
        var row = await t.RowAsync(launch.Id);
        Assert.NotNull(row.WaitingRoomClosedAt);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task L21_lower_admission_rate_mid_launch()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, rate: 10, products: new SeedProduct(2, 100));
        await QueueAsync(t, launch.Id, 30);
        Assert.Equal(10, (await t.TickAsync(launch.Id)).Admitted.Count);

        await t.Admin.SetAdmissionRateAsync(new() { LaunchId = launch.Id.ToString("D"), AdmissionRate = 2, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(2, (await t.TickAsync(launch.Id)).Admitted.Count);
    }

    [Fact]
    public async Task Scheduler_opens_preorder_at_launch_time()
    {
        await using var t = await StartAsync();
        var due = await Seed.LaunchAsync(t, LaunchStages.Announced);
        var later = await Seed.LaunchAsync(t, LaunchStages.Announced);
        await t.SqlAsync($"UPDATE launches SET launch_time = now() - interval '1 second' WHERE id = {due.Id}");
        await t.Redis.StringSetAsync(RedisKeys.State(due.Id), "stale");

        await t.RunSchedulerAsync();

        var row = await t.RowAsync(due.Id);
        Assert.Equal(LaunchStages.PreorderOpen, row.Stage);
        Assert.Equal(1, row.Version);
        Assert.Equal(LaunchStages.Announced, (await t.RowAsync(later.Id)).Stage);
        Assert.Equal(1, await StageEventsAsync(t, LaunchStages.PreorderOpen, due.Id));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.State(due.Id)));

        await t.RunSchedulerAsync();

        Assert.Equal(1, (await t.RowAsync(due.Id)).Version);
        Assert.Equal(1, await StageEventsAsync(t, LaunchStages.PreorderOpen));
    }

    [Fact]
    public async Task L15_release_date_reached()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var queued = Guid.NewGuid();
        await Join(t, launch.Id, queued);
        await Seed.PassAsync(t, launch.Id, Guid.NewGuid());
        await t.SqlAsync($"UPDATE launches SET launch_time = now() - interval '1 hour', release_date = now() - interval '1 second' WHERE id = {launch.Id}");

        await t.RunSchedulerAsync();

        var row = await t.RowAsync(launch.Id);
        Assert.Equal(LaunchStages.Released, row.Stage);
        Assert.NotNull(row.WaitingRoomClosedAt);
        Assert.Equal(1, row.Version);
        Assert.Equal(1, await StageEventsAsync(t, LaunchStages.Released, launch.Id));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Queue(launch.Id)));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Passes(launch.Id)));
        Assert.Equal("closed", (await Status(t, launch.Id, queued)).State);
    }

    [Fact]
    public async Task Scheduler_transitions_once_across_replicas()
    {
        await using var t = await StartAsync();
        var launches = new List<SeededLaunch>();
        for (var i = 0; i < 3; i++) launches.Add(await Seed.LaunchAsync(t, LaunchStages.Announced));
        foreach (var l in launches) await t.SqlAsync($"UPDATE launches SET launch_time = now() - interval '2 hours', release_date = now() - interval '1 second' WHERE id = {l.Id}");

        await Task.WhenAll(t.RunSchedulerAsync(), t.RunSchedulerAsync());

        // Both thresholds are past, so each launch went through both transitions, one event each, one version bump each.
        foreach (var l in launches)
        {
            Assert.Equal(1, await StageEventsAsync(t, LaunchStages.PreorderOpen, l.Id));
            Assert.Equal(1, await StageEventsAsync(t, LaunchStages.Released, l.Id));
            var row = await t.RowAsync(l.Id);
            Assert.Equal(LaunchStages.Released, row.Stage);
            Assert.Equal(2, row.Version);
        }
    }

    [Fact]
    public async Task Cleanup_retries_until_the_keys_are_gone()
    {
        await using var t = await StartAsync();
        var recent = await Seed.LaunchAsync(t);
        var old = await Seed.LaunchAsync(t);
        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() - interval '1 minute' WHERE id = {recent.Id}");
        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() - interval '2 hours' WHERE id = {old.Id}");
        await QueueAsync(t, recent.Id, 1);
        await QueueAsync(t, old.Id, 1);

        await t.RunAdmissionAsync();

        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Queue(recent.Id)));
        Assert.True(await t.Redis.KeyExistsAsync(RedisKeys.Queue(old.Id)));
    }

    [Fact]
    public async Task One_failing_launch_does_not_stop_the_others()
    {
        await using var t = await StartAsync();
        var broken = await Seed.LaunchAsync(t);
        var healthy = await Seed.LaunchAsync(t);
        await t.Redis.StringSetAsync(RedisKeys.Queue(broken.Id), "not a sorted set");
        await QueueAsync(t, healthy.Id, 3);

        await t.RunAdmissionAsync();

        Assert.Equal(3, await t.Redis.SortedSetLengthAsync(RedisKeys.Passes(healthy.Id)));
        Assert.Equal(1, t.ErrorLogs);
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains(broken.Id.ToString("D")));
        await t.Redis.KeyDeleteAsync(RedisKeys.Queue(broken.Id));
    }
}
