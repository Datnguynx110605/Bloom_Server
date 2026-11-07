using System.Text.Json;
using Bloom.Fulfillment.Data;
using Microsoft.EntityFrameworkCore;
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

        await Seed.RunLabelWorkerAsync(t);

        var pending = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.LabelPending, pending.Status);
        Assert.Equal(1, pending.LabelAttempts);
        Assert.Equal(t.Clock.GetUtcNow().AddSeconds(1), pending.NextAttemptAt, TimeSpan.FromMilliseconds(1));
        Assert.Single(t.Shippo.Transactions); // Shippo stored it although the call timed out

        t.Clock.Advance(TimeSpan.FromSeconds(1));
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
            await Seed.RunLabelWorkerAsync(t);

            var shipment = await Seed.ShipmentAsync(t, shipmentId);
            Assert.Equal(ShipmentStatuses.LabelPending, shipment.Status);
            Assert.Equal(n, shipment.LabelAttempts);
            Assert.Equal(t.Clock.GetUtcNow() + delays[n - 1], shipment.NextAttemptAt, TimeSpan.FromMilliseconds(1));
            Assert.Equal(calls + 1, t.Shippo.Calls("")); // the failed lookup
            Assert.Equal(n >= 5 ? 1 : 0, metrics.Sum("labels_total", ("result", "failed")));
            Assert.Equal(n >= 5, ErrorLogged());

            t.Clock.Advance(delays[n - 1] / 2); // not due yet: no call
            await Seed.RunLabelWorkerAsync(t);
            Assert.Equal(calls + 1, t.Shippo.Calls(""));
            t.Clock.Advance(delays[n - 1] / 2);
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
        t.Clock.Advance(TimeSpan.FromSeconds(1)); // the good row is due later: the poisoned one is served first
        var good = Seed.Ready();
        await Seed.ReadyAsync(t, good);
        var goodId = (await Seed.PackedAsync(t, good, "SN5", "SN6")).ShipmentId;

        await Seed.RunLabelWorkerAsync(t);

        var bad = await Seed.ShipmentAsync(t, poisonedId);
        Assert.Equal(ShipmentStatuses.LabelPending, bad.Status);
        Assert.Equal(1, bad.LabelAttempts);
        Assert.Equal(t.Clock.GetUtcNow().AddSeconds(1), bad.NextAttemptAt, TimeSpan.FromMilliseconds(1));
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
