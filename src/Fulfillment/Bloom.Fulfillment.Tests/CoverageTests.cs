using System.Text.Json;
using Bloom.Fulfillment.Consumers;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Worker;
using Bloom.Identity.Contracts;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Fulfillment.Tests;

public class CoverageTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A paid order whose plan covers the first of two fresh serials (an open coverage keeps its serial).</summary>
    private static async Task<(OrderReadyToFulfillV1 Order, string[] Serials, TestCaller Customer)> PaidAsync(TestFulfillment t)
    {
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        return (ready, [$"CVA{id}", $"CVB{id}"], TestCaller.Customer(ready.AccountId));
    }

    private static Task<HttpResponseMessage> CustomerGetAsync(TestFulfillment t, string serial, TestCaller caller) =>
        t.SendAsync(HttpMethod.Get, $"/api/warranty/{Uri.EscapeDataString(serial)}", null, caller);

    private static Task<HttpResponseMessage> StaffGetAsync(TestFulfillment t, string serial, string role) =>
        t.SendAsync(HttpMethod.Get, $"/api/admin/fulfillment/warranty/{Uri.EscapeDataString(serial)}", null, TestCaller.Staff(role));

    private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    [Fact]
    public async Task Customer_sees_own_coverage_by_serial()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await PaidAsync(t);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, order, serials);

        using var pending = await CustomerGetAsync(t, $" {serials[0].ToLowerInvariant()} ", customer);
        var body = await pending.OkJsonAsync();
        Assert.Equal((serials[0], order.OrderId.ToString(), "pending_delivery", 12), (body.GetProperty("serial").GetString(), body.GetProperty("orderId").GetString(), body.GetProperty("status").GetString(), body.GetProperty("coverageMonths").GetInt32()));
        Assert.False(Has(body, "startsAt") || Has(body, "endsAt"));

        using (var handover = await t.SendAsync(HttpMethod.Post, "/api/admin/fulfillment/handover", new { shipmentId }, TestCaller.Staff("warehouse"))) await handover.OkJsonAsync();
        await using (var db = t.NewDb())
            await Seed.TrackAsync(t, (await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId, Ct)).TrackingNumber!, "DELIVERED");
        var deliveredAt = t.Clock.GetUtcNow();

        using var active = await CustomerGetAsync(t, serials[0], customer);
        body = await active.OkJsonAsync();
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal(deliveredAt, body.GetProperty("startsAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(deliveredAt.AddMonths(12), body.GetProperty("endsAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task D30_coverage_past_end_reads_expired()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await PaidAsync(t);
        await Seed.DeliveredAsync(t, order, serials);

        t.Clock.Advance(TimeSpan.FromDays(364));
        using (var before = await CustomerGetAsync(t, serials[0], customer)) Assert.Equal("active", (await before.OkJsonAsync()).GetProperty("status").GetString());

        t.Clock.Advance(TimeSpan.FromDays(2)); // past ends_at (delivery + 12 months)
        using (var customerView = await CustomerGetAsync(t, serials[0], customer)) Assert.Equal("expired", (await customerView.OkJsonAsync()).GetProperty("status").GetString());
        using (var staffView = await StaffGetAsync(t, serials[0], "support")) Assert.Equal("expired", (await staffView.OkJsonAsync()).GetProperty("status").GetString());

        await using var db = t.NewDb();
        Assert.Equal(CoverageStatuses.Active, (await db.WarrantyCoverages.AsNoTracking().SingleAsync(c => c.Serial == serials[0], Ct)).Status); // nothing stored changes
    }

    [Fact]
    public async Task Other_customers_serial_is_NOT_FOUND()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, _) = await PaidAsync(t);
        await Seed.LabelReadyAsync(t, order, serials);
        var stranger = TestCaller.Customer();

        using var others = await CustomerGetAsync(t, serials[0], stranger);
        var othersBody = await others.Content.ReadAsStringAsync(Ct);
        await others.ExpectErrorAsync(404, "NOT_FOUND");
        using var unknown = await CustomerGetAsync(t, "NOSUCHSERIAL1", stranger);
        var unknownBody = await unknown.Content.ReadAsStringAsync(Ct);
        await unknown.ExpectErrorAsync(404, "NOT_FOUND");

        Assert.Equal(unknownBody, othersBody); // Review Focus 4: nothing tells the two apart
    }

    [Fact]
    public async Task Cancelled_coverage_is_shown_when_no_open_one()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await PaidAsync(t);
        await Seed.DeliveredAsync(t, order, serials);
        await Seed.ReturnAsync(t, order.OrderId, customer, (2, 1)); // plan-only: decided at once, the coverage ends

        using var response = await CustomerGetAsync(t, serials[0], customer);
        var body = await response.OkJsonAsync();

        Assert.Equal(("cancelled", serials[0]), (body.GetProperty("status").GetString(), body.GetProperty("serial").GetString()));
        Assert.True(Has(body, "startsAt"));
    }

    [Fact]
    public async Task Staff_lookup_needs_support_or_admin()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, _) = await PaidAsync(t);
        await Seed.LabelReadyAsync(t, order, serials);

        using (var warehouse = await StaffGetAsync(t, serials[0], "warehouse")) await warehouse.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        foreach (var role in new[] { "support", "admin" })
        {
            using var response = await StaffGetAsync(t, serials[0], role);
            var body = await response.OkJsonAsync();
            Assert.Equal(("pending_delivery", order.OrderId.ToString()), (body.GetProperty("status").GetString(), body.GetProperty("orderId").GetString()));
        }
        using var unknown = await StaffGetAsync(t, "NOSUCHSERIAL1", "support");
        await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using var bad = await StaffGetAsync(t, "bad serial!", "support");
        await bad.ExpectValidationAsync("serial");
    }

    [Fact]
    public async Task Account_deleted_anonymizes_ship_to()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, _) = await PaidAsync(t);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, order, serials);
        var other = Seed.Ready();
        await Seed.ReadyAsync(t, other);

        await Consume.Async<AccountDeletedConsumer>(t, IdentityMessages.AccountDeleted, new AccountDeletedV1(order.AccountId));
        await Consume.Async<AccountDeletedConsumer>(t, IdentityMessages.AccountDeleted, new AccountDeletedV1(Guid.NewGuid())); // unknown account: no-op

        var shipTo = JsonSerializer.Deserialize<ShipToV1>((await Seed.TaskAsync(t, order.OrderId))!.ShipTo!, JsonSerializerOptions.Web)!;
        Assert.Equal(("[deleted]", "[deleted]"), (shipTo.Name, shipTo.Phone));
        Assert.Equal(["[deleted]", "[deleted]"], shipTo.Lines);
        Assert.Equal(("Reno", "NV", "89501"), (shipTo.City, shipTo.State, shipTo.Zip));
        var untouched = JsonSerializer.Deserialize<ShipToV1>((await Seed.TaskAsync(t, other.OrderId))!.ShipTo!, JsonSerializerOptions.Web)!;
        Assert.Equal("Ann Lee", untouched.Name);
        Assert.Equal(other.ShipTo.Lines, untouched.Lines);
        await using var db = t.NewDb();
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId, Ct);
        Assert.StartsWith("TRK", shipment.TrackingNumber);
        Assert.Equal(order.TotalCents, (await db.OrderAccounts.AsNoTracking().SingleAsync(a => a.OrderId == order.OrderId, Ct)).TotalCents);
        Assert.Equal(order.Lines.Select(l => l.PaidCents), (await db.OrderLineAccounts.AsNoTracking().Where(l => l.OrderId == order.OrderId).OrderBy(l => l.LineNo).ToListAsync(Ct)).Select(l => l.PaidCents));
    }

    [Fact]
    public async Task Daily_check_voids_orphan_and_duplicate_labels()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (order, serials, _) = await PaidAsync(t);
        var (_, shipmentId) = await Seed.LabelReadyAsync(t, order, serials);
        var own = t.Shippo.Transactions.Single();
        var orphan = t.Shippo.AddLabel($"bloom:{Guid.NewGuid()}");
        var duplicate = t.Shippo.AddLabel($"bloom:{shipmentId}");
        var sweeps = ActivatorUtilities.CreateInstance<Sweeps>(t.Services);

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        await sweeps.RunLabelCheckAsync(Ct);
        Assert.Empty(t.Shippo.Refunds); // a 5-minute-old label may still be on its way into the shipment row

        t.Clock.Advance(TimeSpan.FromMinutes(6));
        await sweeps.RunLabelCheckAsync(Ct);

        Assert.Equal([orphan.TransactionId, duplicate.TransactionId], t.Shippo.Refunds.Order());
        Assert.DoesNotContain(own.TransactionId, t.Shippo.Refunds);
        Assert.Equal(2, metrics.Sum("labels_total", ("result", "voided")));
        Assert.Equal(2, t.Logs.Entries.Count(e => e.StartsWith("[Error]") && e.Contains("Orphan or duplicate Shippo label")));
    }

    [Fact]
    public async Task Daily_check_keeps_the_label_of_a_label_pending_shipment()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, _) = await PaidAsync(t);
        var (_, shipmentId) = await Seed.PackedAsync(t, order, serials); // label_pending: the buy timed out, Shippo holds the label, the worker will reuse it (F-06)
        var label = t.Shippo.AddLabel($"bloom:{shipmentId}");
        var sweeps = ActivatorUtilities.CreateInstance<Sweeps>(t.Services);

        t.Clock.Advance(TimeSpan.FromMinutes(11));
        await sweeps.RunLabelCheckAsync(Ct);

        Assert.Empty(t.Shippo.Refunds);
        await Seed.RunLabelWorkerAsync(t);
        await using var db = t.NewDb();
        Assert.Equal(label.TransactionId, (await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId, Ct)).ShippoTransactionId);
    }
}
