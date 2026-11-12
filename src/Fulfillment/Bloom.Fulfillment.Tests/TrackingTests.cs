using System.Diagnostics;
using System.Net;
using System.Text;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Worker;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bloom.Fulfillment.Tests;

public class TrackingTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Warehouse = TestCaller.Staff("warehouse");

    private static async Task<(OrderReadyToFulfillV1 Order, Guid ShipmentId, string Tracking)> HandedOverAsync(TestFulfillment t, string serialSuffix = "")
    {
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, ready, "SNB" + serialSuffix, "SNA" + serialSuffix);
        using (var response = await t.SendAsync(HttpMethod.Post, "/api/admin/fulfillment/handover", new { shipmentId }, Warehouse)) await response.OkJsonAsync();
        return (ready, shipmentId, (await Seed.ShipmentAsync(t, shipmentId)).TrackingNumber!);
    }

    private static Task<HttpResponseMessage> PostAsync(TestFulfillment t, string body, string secret = "secret-test") =>
        t.Http.PostAsync($"/webhooks/shippo/{secret}", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

    private static string Body(string tracking, string claimed) =>
        $$$$"""{"event":"track_updated","data":{"tracking_number":"{{{{tracking}}}}","tracking_status":{"status":"{{{{claimed}}}}"}}}""";

    /// <summary>Shippo now reports <paramref name="status"/> and sends the event; asserts HTTP 200.</summary>
    private static async Task EventAsync(TestFulfillment t, string tracking, string status)
    {
        t.Shippo.SetTracking(tracking, status);
        using var response = await PostAsync(t, Body(tracking, status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task SweepAsync(TestFulfillment t) => ActivatorUtilities.CreateInstance<Sweeps>(t.Services).RunPossiblyLostAsync(Ct);

    private static async Task DismissAsync(TestFulfillment t, Guid shipmentId)
    {
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM shipments WHERE id = {shipmentId} FOR UPDATE", Ct);
        await scope.ServiceProvider.GetRequiredService<Failures>().DismissAsync(shipmentId, Ct);
        await tx.CommitAsync(Ct);
    }

    [Fact]
    public async Task Webhook_secret_stays_out_of_the_trace()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var paths = new List<string?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => // the listener sees every test class's requests: keep only webhook ones
            {
                if (a.GetTagItem("url.path") is string path && path.StartsWith("/webhooks/shippo/")) lock (paths) paths.Add(path);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var response = await PostAsync(t, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // the secret was right: the body has no tracking number
        await Poll.UntilAsync(() => { lock (paths) return Task.FromResult(paths.Count > 0); }, 5, "the request span to stop"); // it stops just after the response is sent
        lock (paths) Assert.Equal(["/webhooks/shippo/{secret}"], paths.Distinct());
    }

    [Fact]
    public async Task F4_webhook_secret_absent_from_request_logs()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        // The app's own filter decides what reaches the capture: appsettings.json sets Microsoft.AspNetCore to Warning, which hides "Request starting/finished" (Information).
        Assert.Equal("Warning", t.Services.GetRequiredService<IConfiguration>()["Logging:LogLevel:Microsoft.AspNetCore"]);

        using (var ok = await PostAsync(t, "{}")) Assert.Equal(HttpStatusCode.BadRequest, ok.StatusCode); // right secret
        using (var wrong = await PostAsync(t, "{}", "wrong-secret")) Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        var loggers = t.Services.GetRequiredService<ILoggerFactory>(); // the capture sees an Information entry of another category, and none of ASP.NET Core's
        loggers.CreateLogger("Bloom.Test").LogInformation("control");
        loggers.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("Request starting control");
        Assert.Contains(t.Logs.Entries, e => e == "[Information] control");
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("secret-test"));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("Request starting") || e.Contains("Request finished"));
    }

    [Fact]
    public async Task Returned_on_possibly_lost_shipment()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        await EventAsync(t, tracking, "FAILURE");
        Assert.Equal(FailureKinds.Lost, (await Seed.FailureAsync(t, shipmentId))!.Kind);

        await EventAsync(t, tracking, "RETURNED");

        Assert.Equal(ShipmentStatuses.ReturnedToSender, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        var failure = (await Seed.FailureAsync(t, shipmentId))!; // the same row, now a confirmed return to sender
        Assert.Equal((FailureKinds.ReturnedToSender, FailureStatuses.Confirmed, null), (failure.Kind, failure.Status, failure.PreviousStatus));
    }

    [Fact]
    public async Task Sweep_row_failure_does_not_stop_others()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, poisonedId, _) = await HandedOverAsync(t, "1");
        var (_, goodId, _) = await HandedOverAsync(t, "2");
        await Seed.PoisonAsync(t, "shipments", poisonedId);

        t.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await SweepAsync(t); // doesn't throw

        Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, poisonedId)).Status); // rolled back
        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, goodId)).Status);
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("failed") && e.Contains(poisonedId.ToString()));
    }

    [Fact]
    public async Task Transit_then_delivered()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (order, shipmentId, tracking) = await HandedOverAsync(t);

        await EventAsync(t, tracking, "TRANSIT");
        var transit = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.InTransit, transit.Status);
        Assert.Equal(t.Clock.GetUtcNow(), transit.LastTrackingAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.All(await Seed.CoveragesAsync(t, order.OrderId), c => Assert.Equal(CoverageStatuses.PendingDelivery, c.Status));

        t.Clock.Advance(TimeSpan.FromDays(2));
        await EventAsync(t, tracking, "DELIVERED");

        var now = t.Clock.GetUtcNow();
        var delivered = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.Delivered, delivered.Status);
        Assert.Equal(now, delivered.DeliveredAt!.Value, TimeSpan.FromMilliseconds(1));
        await using (var db = t.NewDb())
            Assert.Equal(now, (await db.OrderAccounts.AsNoTracking().SingleAsync(a => a.OrderId == order.OrderId, Ct)).DeliveredAt!.Value, TimeSpan.FromMilliseconds(1));
        var coverages = await Seed.CoveragesAsync(t, order.OrderId);
        Assert.NotEmpty(coverages);
        Assert.All(coverages, c =>
        {
            Assert.Equal(CoverageStatuses.Active, c.Status);
            Assert.Equal(now, c.StartsAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal(c.StartsAt!.Value.UtcDateTime.AddMonths(12), c.EndsAt!.Value.UtcDateTime, TimeSpan.FromMilliseconds(1));
        });
        var message = Assert.Single(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
        Assert.Equal((order.OrderId, order.OrderId), (message.OrderId, message.RefId));
        Assert.Equal(now, message.DeliveredAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, metrics.Sum("tracking_webhooks_total", ("result", "applied")));
    }

    [Fact]
    public Task D32_transit_scan_on_label_ready_hands_over_first() => HandsOverFirstAsync("TRANSIT", ShipmentStatuses.InTransit);

    [Fact]
    public Task D32_delivered_scan_on_label_ready_hands_over_first() => HandsOverFirstAsync("DELIVERED", ShipmentStatuses.Delivered);

    private async Task HandsOverFirstAsync(string scan, string expected)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (taskId, shipmentId) = await Seed.LabelReadyAsync(t, ready, "SNB", "SNA");
        var tracking = (await Seed.ShipmentAsync(t, shipmentId)).TrackingNumber!;
        t.Clock.Advance(TimeSpan.FromHours(3));

        await EventAsync(t, tracking, scan);

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(expected, shipment.Status);
        Assert.Equal(t.Clock.GetUtcNow(), shipment.HandedOverAt!.Value, TimeSpan.FromMilliseconds(1));
        await using (var db = t.NewDb()) Assert.Equal(TaskStatuses.HandedOver, (await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == taskId, Ct)).Status);
        var shipped = Assert.Single(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
        Assert.Equal((ready.OrderId, tracking), (shipped.OrderId, shipped.TrackingNumber));
        Assert.Equal(["SNB", "SNA"], Assert.Single(shipped.Lines).Serials);
        Assert.Equal(scan == "DELIVERED" ? 1 : 0, (await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered)).Count);
    }

    [Fact]
    public async Task D34_delivered_on_lost_sets_delivered_at_keeps_lost_logs_error()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, shipmentId, tracking) = await HandedOverAsync(t);
        await using (var db = t.NewDb()) await db.Shipments.Where(s => s.Id == shipmentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ShipmentStatuses.Lost), Ct);
        int Outbox() { using var db = t.NewDb(); return db.Set<Bloom.Shared.Persistence.OutboxMessage>().Count(); }
        var before = Outbox();
        t.Clock.Advance(TimeSpan.FromDays(9));

        await EventAsync(t, tracking, "DELIVERED");

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.Lost, shipment.Status);
        Assert.Equal(t.Clock.GetUtcNow(), shipment.DeliveredAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains(shipmentId.ToString()) && e.Contains("lost"));
        Assert.Equal(before, Outbox()); // no stock or money message
        await using var db2 = t.NewDb();
        Assert.Null((await db2.OrderAccounts.AsNoTracking().SingleAsync(a => a.OrderId == order.OrderId, Ct)).DeliveredAt);
    }

    [Fact]
    public async Task F07_forged_webhook_applies_the_refetched_status()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        t.Shippo.SetTracking(tracking, "TRANSIT");

        using var response = await PostAsync(t, Body(tracking, "DELIVERED")); // the body lies
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(ShipmentStatuses.InTransit, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
    }

    [Fact]
    public async Task Repeated_and_late_events_change_nothing()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);

        await EventAsync(t, tracking, "DELIVERED");
        var first = await Seed.ShipmentAsync(t, shipmentId);
        t.Clock.Advance(TimeSpan.FromHours(1));
        await EventAsync(t, tracking, "DELIVERED");
        await EventAsync(t, tracking, "TRANSIT");

        var last = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal((ShipmentStatuses.Delivered, first.DeliveredAt, first.LastTrackingAt, first.Version), (last.Status, last.DeliveredAt, last.LastTrackingAt, last.Version));
        Assert.Single(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Warning]") && e.Contains(shipmentId.ToString()));
        Assert.Equal((1, 2), (metrics.Sum("tracking_webhooks_total", ("result", "applied")), metrics.Sum("tracking_webhooks_total", ("result", "unchanged"))));
    }

    [Fact]
    public async Task Returned_creates_a_confirmed_failure()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);

        await EventAsync(t, tracking, "RETURNED");

        Assert.Equal(ShipmentStatuses.ReturnedToSender, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        var failure = (await Seed.FailureAsync(t, shipmentId))!;
        Assert.Equal((FailureKinds.ReturnedToSender, FailureStatuses.Confirmed), (failure.Kind, failure.Status));
    }

    [Fact]
    public async Task Failure_flags_possibly_lost()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        await EventAsync(t, tracking, "TRANSIT");

        await EventAsync(t, tracking, "FAILURE");

        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        var failure = (await Seed.FailureAsync(t, shipmentId))!;
        Assert.Equal((FailureKinds.Lost, FailureStatuses.PossiblyLost, ShipmentStatuses.InTransit), (failure.Kind, failure.Status, failure.PreviousStatus));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains(shipmentId.ToString()));
    }

    [Fact]
    public async Task Possibly_lost_then_delivered_dismisses_automatically()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        await EventAsync(t, tracking, "FAILURE");
        t.Clock.Advance(TimeSpan.FromDays(1));

        await EventAsync(t, tracking, "DELIVERED");

        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.Delivered, shipment.Status);
        var failure = (await Seed.FailureAsync(t, shipmentId))!;
        Assert.Equal(FailureStatuses.Dismissed, failure.Status);
        Assert.Equal(t.Clock.GetUtcNow(), failure.DismissedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Single(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
    }

    [Fact]
    public async Task Possibly_lost_then_transit_restores_and_moves_on()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        await EventAsync(t, tracking, "FAILURE");
        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, shipmentId)).Status);

        await EventAsync(t, tracking, "TRANSIT");

        Assert.Equal(ShipmentStatuses.InTransit, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Equal(FailureStatuses.Dismissed, (await Seed.FailureAsync(t, shipmentId))!.Status);
    }

    [Fact]
    public async Task F08_no_tracking_for_seven_days_flags_possibly_lost()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, _) = await HandedOverAsync(t);

        t.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        await SweepAsync(t);
        Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Null(await Seed.FailureAsync(t, shipmentId));

        t.Clock.Advance(TimeSpan.FromMinutes(3));
        await SweepAsync(t);

        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        var failure = (await Seed.FailureAsync(t, shipmentId))!;
        Assert.Equal((FailureKinds.Lost, FailureStatuses.PossiblyLost, ShipmentStatuses.HandedOver), (failure.Kind, failure.Status, failure.PreviousStatus));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains(shipmentId.ToString()));
    }

    [Fact]
    public async Task Dismissed_shipment_waits_seven_days_from_dismissal()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);
        await EventAsync(t, tracking, "TRANSIT");
        t.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await SweepAsync(t);
        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, shipmentId)).Status);

        t.Clock.Advance(TimeSpan.FromDays(1));
        await DismissAsync(t, shipmentId);
        var dismissed = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.InTransit, dismissed.Status);
        Assert.Equal(FailureStatuses.Dismissed, (await Seed.FailureAsync(t, shipmentId))!.Status);

        t.Clock.Advance(TimeSpan.FromDays(6)); // 6 days after the dismissal
        await SweepAsync(t);
        Assert.Equal(ShipmentStatuses.InTransit, (await Seed.ShipmentAsync(t, shipmentId)).Status);

        t.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1)); // 7 days + 1 min after the dismissal
        await SweepAsync(t);

        Assert.Equal(ShipmentStatuses.PossiblyLost, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        await using var db = t.NewDb();
        var failure = await db.DeliveryFailures.AsNoTracking().SingleAsync(f => f.ShipmentId == shipmentId, Ct); // the same row
        Assert.Equal((FailureStatuses.PossiblyLost, ShipmentStatuses.InTransit), (failure.Status, failure.PreviousStatus));
    }

    [Fact]
    public async Task Webhook_guards()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, shipmentId, tracking) = await HandedOverAsync(t);

        using (var wrong = await PostAsync(t, Body(tracking, "TRANSIT"), "wrong")) Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        using (var get = await t.Http.GetAsync("/webhooks/shippo/secret-test", Ct)) Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var big = await PostAsync(t, $$"""{"pad":"{{new string('x', 70 * 1024)}}"}""")) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        using (var none = await PostAsync(t, """{"event":"track_updated","data":{}}""")) Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using (var junk = await PostAsync(t, "not json")) Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        using (var unknown = await PostAsync(t, Body("NOPE", "TRANSIT"))) Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        t.Shippo.FailNext(1);
        using (var failed = await PostAsync(t, Body(tracking, "TRANSIT"))) Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Equal((1, 1, 3, 1), (
            metrics.Sum("tracking_webhooks_total", ("result", "bad_secret")), metrics.Sum("tracking_webhooks_total", ("result", "unknown")),
            metrics.Sum("tracking_webhooks_total", ("result", "invalid")), metrics.Sum("tracking_webhooks_total", ("result", "error"))));
    }

    [Fact]
    public async Task Ship_back_follows_tracking_only()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var id = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using (var db = t.NewDb())
        {
            db.Shipments.Add(new Shipment
            {
                Id = id, OrderId = orderId, ReturnRequestId = Guid.NewGuid(), Direction = Directions.ShipBack, Service = ShippingOptions.Standard, Status = ShipmentStatuses.HandedOver,
                TrackingNumber = "SB1", HandedOverAt = t.Clock.GetUtcNow(), NextAttemptAt = t.Clock.GetUtcNow(), CreatedAt = t.Clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(Ct);
        }

        await EventAsync(t, "SB1", "TRANSIT");
        Assert.Equal(ShipmentStatuses.InTransit, (await Seed.ShipmentAsync(t, id)).Status);
        await EventAsync(t, "SB1", "FAILURE"); // nothing else applies
        Assert.Equal(ShipmentStatuses.InTransit, (await Seed.ShipmentAsync(t, id)).Status);
        await EventAsync(t, "SB1", "DELIVERED");

        Assert.Equal(ShipmentStatuses.Delivered, (await Seed.ShipmentAsync(t, id)).Status);
        Assert.Null(await Seed.FailureAsync(t, id));
        Assert.Empty(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
        Assert.Empty(await Seed.CoveragesAsync(t, orderId));
    }
}
