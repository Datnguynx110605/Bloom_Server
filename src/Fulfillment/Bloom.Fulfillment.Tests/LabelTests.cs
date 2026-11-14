using System.Data.Common;
using System.Text.Json;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Shippo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bloom.Fulfillment.Tests;

public class LabelTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Guid OrderId, Guid ShipmentId)> PackedAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var ready = Seed.Ready(configure);
        await Seed.ReadyAsync(t, ready);
        return (ready.OrderId, (await Seed.PackedAsync(t, ready, "SN1", "SN2")).ShipmentId);
    }

    private static Task CancelAsync(TestFulfillment t, Guid orderId) =>
        t.Internal.CancelPackTaskAsync(new() { OrderId = orderId.ToString("D"), By = "support" }, cancellationToken: Ct).ResponseAsync;

    [Fact]
    public async Task Worker_buys_the_label()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, ready, "SN1", "SN2");

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelReady, shipment.Status);
        var bought = t.Shippo.Transactions.Single();
        Assert.Equal(bought.TransactionId, shipment.ShippoTransactionId);
        Assert.Equal("TRK1", shipment.TrackingNumber);
        Assert.Equal("https://labels.test/1.pdf", shipment.LabelUrl);
        Assert.Equal(0, shipment.LabelAttempts);
        Assert.Equal($"bloom:{shipmentId}", bought.Metadata);
        Assert.Equal(["usps_ground_advantage"], t.Shippo.BoughtTokens);

        var request = t.Shippo.ShipmentRequests.Single();
        Assert.Equal($"bloom:{shipmentId}", request.GetProperty("metadata").GetString());
        var from = request.GetProperty("address_from");
        Assert.Equal(("Bloom Warehouse", "1 Warehouse Way", "Reno", "NV", "89501", "+17755550100"),
            (from.GetProperty("name").GetString(), from.GetProperty("street1").GetString(), from.GetProperty("city").GetString(),
                from.GetProperty("state").GetString(), from.GetProperty("zip").GetString(), from.GetProperty("phone").GetString()));
        var to = request.GetProperty("address_to");
        Assert.Equal(("Ann Lee", "1 Main St", "Apt 2", "Reno", "NV", "89501", "+17755550123"),
            (to.GetProperty("name").GetString(), to.GetProperty("street1").GetString(), to.GetProperty("street2").GetString(), to.GetProperty("city").GetString(),
                to.GetProperty("state").GetString(), to.GetProperty("zip").GetString(), to.GetProperty("phone").GetString()));
        var parcel = request.GetProperty("parcels").EnumerateArray().Single();
        Assert.Equal((10m, 8m, 4m, 32m),
            (parcel.GetProperty("length").GetDecimal(), parcel.GetProperty("width").GetDecimal(), parcel.GetProperty("height").GetDecimal(), parcel.GetProperty("weight").GetDecimal()));
        Assert.False(request.TryGetProperty("extra", out _)); // not a return label
        Assert.Equal(1, metrics.Sum("labels_total", ("result", "bought")));
    }

    [Fact]
    public async Task Express_shipment_uses_the_express_token()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready(b => b.Express(1500));
        await Seed.ReadyAsync(t, ready);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, ready, "SN1", "SN2");

        Assert.Equal(["usps_priority"], t.Shippo.BoughtTokens);
        Assert.Equal(ShipmentStatuses.LabelReady, (await Seed.ShipmentAsync(t, shipmentId)).Status);
    }

    [Fact]
    public async Task F06_label_buy_times_out_then_retry_reuses_the_label()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.TimeoutAfterStoringNextTransaction = true;
        var before = await t.DbNowAsync();

        await Seed.RunLabelWorkerAsync(t);
        var after = await t.DbNowAsync();

        var pending = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelPending, pending.Status);
        Assert.Equal(1, pending.LabelAttempts);
        Assert.InRange(pending.NextAttemptAt, before.AddSeconds(1), after.AddSeconds(1));
        Assert.Single(t.Shippo.Transactions); // Shippo stored it although the call timed out

        await t.AgeAsync(TimeSpan.FromSeconds(1));
        await Seed.RunLabelWorkerAsync(t);

        var ready = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelReady, ready.Status);
        Assert.Equal(t.Shippo.Transactions.Single().TransactionId, ready.ShippoTransactionId);
        Assert.Equal(0, ready.LabelAttempts);
        Assert.Single(t.Shippo.Transactions); // reused, not bought again
        Assert.Equal(1, metrics.Sum("labels_total", ("result", "reused")));
        Assert.Equal(0, metrics.Sum("labels_total", ("result", "bought")));
    }

    [Fact]
    public async Task Failures_follow_the_retry_schedule()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.FailNext(6);
        TimeSpan[] delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)];
        bool ErrorLogged() => t.Logs.Entries.Any(e => e.StartsWith("[Error]") && e.Contains(shipmentId.ToString()));

        for (var n = 1; n <= 6; n++)
        {
            var calls = t.Shippo.Calls("");
            var before = await t.DbNowAsync();
            await Seed.RunLabelWorkerAsync(t);
            var after = await t.DbNowAsync();

            var shipment = await Seed.ShipmentAsync(t, shipmentId);
            Assert.Equal(ShipmentStatuses.LabelPending, shipment.Status);
            Assert.Equal(n, shipment.LabelAttempts);
            Assert.InRange(shipment.NextAttemptAt, before + delays[n - 1], after + delays[n - 1]);
            Assert.Equal(calls + 1, t.Shippo.Calls("")); // the failed lookup
            Assert.Equal(n >= 5 ? 1 : 0, metrics.Sum("labels_total", ("result", "failed")));
            Assert.Equal(n >= 5, ErrorLogged());

            await using (var db = t.NewDb()) await db.Database.ExecuteSqlAsync($"UPDATE shipments SET next_attempt_at = now() + interval '1 hour' WHERE id = {shipmentId}", Ct); // pinned, so "not due yet" needs no margin on the short delays
            await Seed.RunLabelWorkerAsync(t); // not due yet: no call
            Assert.Equal(calls + 1, t.Shippo.Calls(""));
            await using (var db = t.NewDb()) await db.Database.ExecuteSqlAsync($"UPDATE shipments SET next_attempt_at = now() - interval '1 second' WHERE id = {shipmentId}", Ct); // the delay has passed
        }

        await Seed.RunLabelWorkerAsync(t); // Shippo is back
        var done = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelReady, done.Status);
        Assert.Equal(0, done.LabelAttempts);
        Assert.Equal(1, metrics.Sum("labels_total", ("result", "bought")));
    }

    [Fact]
    public async Task Void_pending_is_voided()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, ready, "SN1", "SN2");

        await CancelAsync(t, ready.OrderId);
        Assert.Equal(ShipmentStatuses.VoidPending, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        await Seed.RunLabelWorkerAsync(t);

        Assert.Equal(ShipmentStatuses.Voided, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Equal([t.Shippo.Transactions.Single().TransactionId], t.Shippo.Refunds);
        Assert.Equal(1, metrics.Sum("labels_total", ("result", "voided")));
    }

    [Fact]
    public async Task Cancel_while_label_pending_buys_nothing() // Review Focus 5
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, shipmentId) = await PackedAsync(t);

        await CancelAsync(t, orderId);
        await Seed.RunLabelWorkerAsync(t);

        Assert.Equal(ShipmentStatuses.Voided, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Empty(t.Shippo.Transactions);
        Assert.Equal(0, t.Shippo.Calls(""));
    }

    [Fact]
    public async Task Cancel_during_buy_voids_the_bought_label() // Review Focus 5
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, shipmentId) = await PackedAsync(t);
        Task cancel = Task.CompletedTask;
        var cancelStarted = false;
        t.Shippo.BeforeTransaction = () =>
        {
            cancelStarted = true;
            cancel = CancelAsync(t, orderId); // not awaited: it must wait for the worker's row lock
            WaitUntilSomeoneWaitsForALock(t.ConnectionString);
        };

        await Seed.RunLabelWorkerAsync(t);

        Assert.True(cancelStarted);
        await cancel; // ok
        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Single(t.Shippo.Transactions);
        var transactionId = t.Shippo.Transactions[0].TransactionId;
        Assert.Equal(transactionId, shipment.ShippoTransactionId);
        // The cancel found label_ready (bought, then taken away): void_pending, or already voided when the worker's own void phase ran after the cancel's commit.
        // Never voided without a refund (FR-05).
        Assert.Contains(shipment.Status, new[] { ShipmentStatuses.VoidPending, ShipmentStatuses.Voided });
        Assert.Equal(shipment.Status == ShipmentStatuses.Voided ? [transactionId] : [], t.Shippo.Refunds);

        t.Shippo.BeforeTransaction = null;
        await Seed.RunLabelWorkerAsync(t);

        Assert.Equal(ShipmentStatuses.Voided, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Equal([transactionId], t.Shippo.Refunds); // voided exactly once
        Assert.Single(t.Shippo.Transactions);
    }

    [Fact]
    public async Task Poisoned_row_backs_off_and_does_not_stall_other_labels_or_voids()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var voidable = Seed.Ready();
        await Seed.ReadyAsync(t, voidable);
        var (_, voidId) = await Seed.LabelReadyAsync(t, voidable, "SN1", "SN2");
        await CancelAsync(t, voidable.OrderId); // void_pending

        var poisoned = Seed.Ready();
        await Seed.ReadyAsync(t, poisoned);
        var poisonedId = (await Seed.PackedAsync(t, poisoned, "SN3", "SN4")).ShipmentId;
        await using (var db = t.NewDb())
            await db.PackTasks.Where(p => p.OrderId == poisoned.OrderId).ExecuteUpdateAsync(p => p.SetProperty(x => x.ShipTo, "{}"), Ct); // an address that can't be read
        var good = Seed.Ready(); // packed after the poisoned one, so it is due later: the poisoned one is served first
        await Seed.ReadyAsync(t, good);
        var goodId = (await Seed.PackedAsync(t, good, "SN5", "SN6")).ShipmentId;
        var before = await t.DbNowAsync();

        await Seed.RunLabelWorkerAsync(t);
        var after = await t.DbNowAsync();

        var bad = await Seed.ShipmentAsync(t, poisonedId);
        Assert.Equal(ShipmentStatuses.LabelPending, bad.Status);
        Assert.Equal(1, bad.LabelAttempts);
        Assert.InRange(bad.NextAttemptAt, before.AddSeconds(1), after.AddSeconds(1));
        Assert.Equal(ShipmentStatuses.LabelReady, (await Seed.ShipmentAsync(t, goodId)).Status);
        Assert.Equal(ShipmentStatuses.Voided, (await Seed.ShipmentAsync(t, voidId)).Status);
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Warning]") && e.Contains(poisonedId.ToString()));
    }

    [Fact]
    public async Task Shippo_label_without_a_tracking_number_fails_the_attempt()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.EmptyTrackingNumberNextTransaction = true;

        await Seed.RunLabelWorkerAsync(t);

        var pending = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelPending, pending.Status);
        Assert.Equal(1, pending.LabelAttempts);
        Assert.Null(pending.TrackingNumber);
    }

    [Fact]
    public async Task F1_list_labels_tolerates_malformed_unrelated_entry()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.AddLabel($"bloom:{Guid.NewGuid()}", trackingNumber: ""); // an unrelated SUCCESS entry without a tracking number
        var own = t.Shippo.AddLabel($"bloom:{shipmentId}");

        var listed = await t.Services.GetRequiredService<IShippo>().ListLabelsAsync(t.Clock.GetUtcNow().AddHours(-1), Ct);
        Assert.Equal(2, listed.Count);

        await Seed.RunLabelWorkerAsync(t);

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal((ShipmentStatuses.LabelReady, own.TransactionId), (shipment.Status, shipment.ShippoTransactionId)); // found and reused, not bought
        Assert.Equal(2, t.Shippo.Transactions.Count);
    }

    [Fact]
    public async Task F1_matched_label_without_a_tracking_number_fails_the_attempt()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.AddLabel($"bloom:{shipmentId}", trackingNumber: "");

        await Seed.RunLabelWorkerAsync(t);

        var pending = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal((ShipmentStatuses.LabelPending, 1), (pending.Status, pending.LabelAttempts));
        Assert.Null(pending.TrackingNumber);
        Assert.Single(t.Shippo.Transactions); // nothing bought on top
    }

    /// <summary>Fails the failure bookkeeping UPDATE (<c>SET label_attempts</c>) <see cref="Failures"/> times: the narrowest seam for a database failure after the Shippo call.</summary>
    private sealed class FailBookkeeping : DbCommandInterceptor
    {
        public int Failures, Hits;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct)
        {
            MaybeFail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct)
        {
            MaybeFail(command); // the retry write is an UPDATE … RETURNING, so it runs as a reader
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        private void MaybeFail(DbCommand command)
        {
            if (!command.CommandText.Contains("SET label_attempts") || Interlocked.Decrement(ref Failures) < 0) return;
            Interlocked.Increment(ref Hits);
            throw new NpgsqlException("injected database failure");
        }
    }

    private static async Task<TestFulfillment> StartWithAsync(Containers containers, FailBookkeeping fail) =>
        await TestFulfillment.StartAsync(containers, o => o.ConfigureServices = s => s.ConfigureDbContext<FulfillmentDbContext>(db => db.AddInterceptors(fail)));

    [Fact]
    public async Task F2_bookkeeping_failure_is_logged_with_both_failures_and_the_next_pass_records_the_attempt()
    {
        var fail = new FailBookkeeping();
        await using var t = await StartWithAsync(containers, fail);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.FailNext(1);
        fail.Failures = 1; // the one bookkeeping attempt of this pass fails

        await Seed.RunLabelWorkerAsync(t);

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal((ShipmentStatuses.LabelPending, 0), (shipment.Status, shipment.LabelAttempts)); // not recorded: still due
        Assert.Equal(1, fail.Hits); // the injection did fire
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("injected database failure") && e.Contains("500")); // the bookkeeping failure and the Shippo failure behind it

        t.Shippo.FailNext(1);
        var before = await t.DbNowAsync();
        await Seed.RunLabelWorkerAsync(t); // the next pass: the attempt is recorded and the row is back in line
        var after = await t.DbNowAsync();

        shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal((ShipmentStatuses.LabelPending, 1), (shipment.Status, shipment.LabelAttempts));
        Assert.InRange(shipment.NextAttemptAt, before.AddSeconds(1), after.AddSeconds(1));
    }

    [Fact]
    public async Task F2_persistent_bookkeeping_failure_does_not_pin_the_row_first()
    {
        var fail = new FailBookkeeping();
        await using var t = await StartWithAsync(containers, fail);
        var (_, poisonedId) = await PackedAsync(t);
        var good = Seed.Ready(); // packed after the poisoned one, so it is due later: the poisoned one is served first
        await Seed.ReadyAsync(t, good);
        var goodId = (await Seed.PackedAsync(t, good, "SN3", "SN4")).ShipmentId;
        t.Shippo.FailNext(1);
        fail.Failures = int.MaxValue; // the bookkeeping never succeeds

        await Seed.RunLabelWorkerAsync(t); // doesn't throw, doesn't spin on the poisoned row

        Assert.Equal(ShipmentStatuses.LabelReady, (await Seed.ShipmentAsync(t, goodId)).Status);
        var poisoned = await Seed.ShipmentAsync(t, poisonedId);
        Assert.Equal((ShipmentStatuses.LabelPending, 0), (poisoned.Status, poisoned.LabelAttempts));
        Assert.Equal(1, fail.Hits); // the poisoned row's one bookkeeping attempt failed
        Assert.Equal(3, t.Shippo.Calls("transactions/")); // the poisoned row's failed lookup once, the good row's lookup and buy
    }

    [Fact]
    public async Task F3_retry_delay_measured_after_shippo_call()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId) = await PackedAsync(t);
        t.Shippo.FailNext(1);
        t.Shippo.OnRequest = () => Thread.Sleep(TimeSpan.FromSeconds(2)); // the call takes 2 s (the database clock cannot be advanced)
        var before = await t.DbNowAsync();

        await Seed.RunLabelWorkerAsync(t);

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(1, shipment.LabelAttempts);
        Assert.True(shipment.NextAttemptAt >= before.AddSeconds(2 + 1), $"{shipment.NextAttemptAt} is not after-call time + 1 s"); // not before-call time + 1 s
    }

    [Fact]
    public async Task Shippo_pagination_follows_next_and_stops()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var start = t.Clock.GetUtcNow();
        for (var i = 0; i < 250; i++) t.Shippo.AddLabel($"bloom:{Guid.NewGuid()}", start.AddSeconds(i)); // 3 pages of 100, newest first
        var shippo = t.Services.GetRequiredService<IShippo>();

        var all = await shippo.ListLabelsAsync(start.AddDays(-1), Ct);

        Assert.Equal(250, all.Count);
        Assert.Equal(start.AddSeconds(249), all[0].CreatedAt); // newest first
        Assert.Equal(3, t.Shippo.Calls("transactions/"));

        var callsBefore = t.Shippo.Calls("transactions/");
        var recent = await shippo.ListLabelsAsync(start.AddSeconds(100), Ct);

        Assert.Equal(150, recent.Count);
        Assert.Equal(2, t.Shippo.Calls("transactions/") - callsBefore); // an entry older than since ends the walk although the page had a next
    }

    /// <summary>Blocks (synchronously: it runs inside the fake's handler) until some session waits for a lock, so the test really exercises the row-lock wait.</summary>
    internal static void WaitUntilSomeoneWaitsForALock(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
            if ((long)cmd.ExecuteScalar()! > 0) return;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the cancel to block on the shipment's row lock.");
            Thread.Sleep(20);
        }
    }
}
