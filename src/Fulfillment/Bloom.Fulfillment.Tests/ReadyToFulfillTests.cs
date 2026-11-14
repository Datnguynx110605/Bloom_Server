using System.Text.Json;
using Bloom.Fulfillment.Consumers;
using Bloom.Fulfillment.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Tests;

public class ReadyToFulfillTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task CancelAsync(TestFulfillment t, Guid orderId, string by) =>
        t.Internal.CancelPackTaskAsync(new() { OrderId = orderId.ToString("D"), By = by }, cancellationToken: Ct).ResponseAsync;

    private static async Task<(int Tasks, int Lines, int Accounts, int LineAccounts)> CountsAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return (
            await db.PackTasks.CountAsync(p => p.RefId == orderId, Ct),
            await db.PackTaskLines.CountAsync(l => db.PackTasks.Any(p => p.Id == l.TaskId && p.RefId == orderId), Ct),
            await db.OrderAccounts.CountAsync(a => a.OrderId == orderId, Ct),
            await db.OrderLineAccounts.CountAsync(a => a.OrderId == orderId, Ct));
    }

    [Fact]
    public async Task Ready_creates_task_lines_and_accounts()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready(b => b.Express(1500));
        var before = await t.DbNowAsync();

        await Seed.ReadyAsync(t, ready);
        var after = await t.DbNowAsync();

        var task = await Seed.TaskAsync(t, ready.OrderId);
        Assert.NotNull(task);
        Assert.Equal((ready.OrderId, TaskKinds.Order, TaskStatuses.Pending, "express", false), (task.OrderId, task.Kind, task.Status, task.ShippingOption, task.CancelledBeforeArrival));
        Assert.InRange(task.CreatedAt, before, after);
        var shipTo = JsonSerializer.Deserialize<ShipToV1>(task.ShipTo!, JsonSerializerOptions.Web)!;
        Assert.Equal(ready.ShipTo.Lines, shipTo.Lines);
        Assert.Equal("Reno", shipTo.City);

        await using var db = t.NewDb();
        var lines = await db.PackTaskLines.Where(l => l.TaskId == task.Id).OrderBy(l => l.LineNo).ToListAsync(Ct);
        Assert.Equal(
            ready.Lines.Select(l => (l.LineNo, l.Kind, l.SkuId, l.ProductId, l.Serialized, l.Quantity, l.ParentLineNo, l.CoverageMonths)),
            lines.Select(l => (l.LineNo, l.Kind, l.SkuId, l.ProductId, l.Serialized, l.Quantity, l.ParentLineNo, l.CoverageMonths)));

        var account = await db.OrderAccounts.SingleAsync(a => a.OrderId == ready.OrderId, Ct);
        Assert.Equal((ready.AccountId, 1500L, 2002L + 9900 + 1500, false, (DateTimeOffset?)null), (account.AccountId, account.ShippingCents, account.TotalCents, account.ShippingRefunded, account.DeliveredAt));
        var accounts = await db.OrderLineAccounts.Where(a => a.OrderId == ready.OrderId).OrderBy(a => a.LineNo).ToListAsync(Ct);
        Assert.Equal(
            ready.Lines.Select(l => (l.LineNo, l.Kind, l.ParentLineNo, l.Quantity, l.PaidCents, l.TaxCents, 0, 0L)),
            accounts.Select(a => (a.LineNo, a.Kind, a.ParentLineNo, a.Quantity, a.PaidCents, a.TaxCents, a.RefundedUnits, a.RefundedCents)));
    }

    [Fact]
    public async Task Duplicate_ready_creates_one_task()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();

        await Seed.ReadyAsync(t, ready);
        await Seed.ReadyAsync(t, ready);

        Assert.Equal((1, 2, 1, 2), await CountsAsync(t, ready.OrderId));
    }

    [Fact]
    public async Task F02_cancel_before_ready_leaves_a_marker_and_the_event_is_ignored()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();

        await CancelAsync(t, ready.OrderId, "customer");

        var marker = await Seed.TaskAsync(t, ready.OrderId);
        Assert.NotNull(marker);
        Assert.Equal((ready.OrderId, TaskKinds.Order, TaskStatuses.Cancelled, true), (marker.OrderId, marker.Kind, marker.Status, marker.CancelledBeforeArrival));

        await Seed.ReadyAsync(t, ready);

        Assert.Equal((1, 0, 0, 0), await CountsAsync(t, ready.OrderId));
        Assert.Equal(TaskStatuses.Cancelled, (await Seed.TaskAsync(t, ready.OrderId))!.Status);
        await CancelAsync(t, ready.OrderId, "support"); // a repeat finds the marker
    }

    [Fact]
    public async Task Customer_cancel_of_pending_task()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);

        await CancelAsync(t, ready.OrderId, "customer");
        var task = (await Seed.TaskAsync(t, ready.OrderId))!;
        Assert.Equal((TaskStatuses.Cancelled, false, 1L), (task.Status, task.CancelledBeforeArrival, task.Version));

        await CancelAsync(t, ready.OrderId, "customer"); // repeat
        Assert.Equal(1L, (await Seed.TaskAsync(t, ready.OrderId))!.Version);
    }

    [Fact]
    public async Task Support_cancel_of_pending_task()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);

        await CancelAsync(t, ready.OrderId, "support");

        Assert.Equal(TaskStatuses.Cancelled, (await Seed.TaskAsync(t, ready.OrderId))!.Status);
    }

    [Theory]
    [InlineData(TaskStatuses.Packing)]
    [InlineData(TaskStatuses.Packed)]
    public async Task Customer_cancel_after_packing_started_is_refused(string status)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        await Seed.SetTaskStatusAsync(t, ready.OrderId, status);

        await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = ready.OrderId.ToString("D"), By = "customer" }, cancellationToken: Ct), "PACKING_STARTED");

        Assert.Equal(status, (await Seed.TaskAsync(t, ready.OrderId))!.Status);
    }

    [Theory]
    [InlineData(TaskStatuses.Packing)]
    [InlineData(TaskStatuses.Packed)]
    public async Task Support_cancel_of_packing_or_packed_task(string status)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        await Seed.SetTaskStatusAsync(t, ready.OrderId, status);

        await CancelAsync(t, ready.OrderId, "support");

        Assert.Equal(TaskStatuses.Cancelled, (await Seed.TaskAsync(t, ready.OrderId))!.Status);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("support")]
    public async Task Cancel_after_handover_is_refused(string by)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        await Seed.SetTaskStatusAsync(t, ready.OrderId, TaskStatuses.HandedOver);

        await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = ready.OrderId.ToString("D"), By = by }, cancellationToken: Ct), "ALREADY_SHIPPED");
    }

    [Fact]
    public async Task Support_cancel_of_packed_task_takes_its_label_away()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        await Seed.SetTaskStatusAsync(t, ready.OrderId, TaskStatuses.Packed);
        var taskId = (await Seed.TaskAsync(t, ready.OrderId))!.Id;
        var other = Seed.Ready();
        await Seed.ReadyAsync(t, other);
        var otherId = (await Seed.TaskAsync(t, other.OrderId))!.Id;
        var ids = new Dictionary<string, Guid>();
        await using (var db = t.NewDb())
        {
            foreach (var (status, owner) in new[] { ("label_pending", taskId), ("label_ready", taskId), ("handed_over", taskId), ("label_ready", otherId) })
            {
                var id = Guid.CreateVersion7();
                ids[$"{status}/{(owner == taskId ? "this" : "other")}"] = id;
                db.Shipments.Add(new Shipment
                {
                    Id = id, PackTaskId = owner, OrderId = owner == taskId ? ready.OrderId : other.OrderId, Direction = Directions.Outbound, Service = ShippingOptions.Standard,
                    Status = status, NextAttemptAt = t.Clock.GetUtcNow(), CreatedAt = t.Clock.GetUtcNow(),
                });
            }
            await db.SaveChangesAsync(Ct);
        }

        await CancelAsync(t, ready.OrderId, "support");

        await using var read = t.NewDb();
        var statuses = await read.Shipments.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Status, Ct);
        Assert.Equal(ShipmentStatuses.Voided, statuses[ids["label_pending/this"]]);
        Assert.Equal(ShipmentStatuses.VoidPending, statuses[ids["label_ready/this"]]);
        Assert.Equal(ShipmentStatuses.HandedOver, statuses[ids["handed_over/this"]]);
        Assert.Equal(ShipmentStatuses.LabelReady, statuses[ids["label_ready/other"]]);
    }

    [Fact]
    public async Task Cancel_validates_input()
    {
        await using var t = await TestFulfillment.StartAsync(containers);

        var orderId = await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = "x", By = "customer" }, cancellationToken: Ct), "VALIDATION_FAILED");
        Assert.Equal("order_id", orderId.Metadata["field"]);
        var by = await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = Guid.NewGuid().ToString("D"), By = "bot" }, cancellationToken: Ct), "VALIDATION_FAILED");
        Assert.Equal("by", by.Metadata["field"]);
    }

    public static TheoryData<string> InvalidPayloads() => new()
    {
        "no lines", "unknown kind", "unknown shipping option", "zero quantity", "negative quantity", "plan without parent", "plan without months",
    };

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task Invalid_ready_payload_is_permanent(string defect)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        var line = ready.Lines[0];
        var plan = ready.Lines[1];
        var invalid = defect switch
        {
            "no lines" => ready with { Lines = [] },
            "unknown kind" => ready with { Lines = [line with { Kind = "gift" }, plan] },
            "unknown shipping option" => ready with { ShippingOption = "drone" },
            "zero quantity" => ready with { Lines = [line with { Quantity = 0 }, plan] },
            "negative quantity" => ready with { Lines = [line, plan with { Quantity = -1 }] },
            "plan without parent" => ready with { Lines = [line, plan with { ParentLineNo = null }] },
            _ => ready with { Lines = [line, plan with { CoverageMonths = null }] },
        };

        await Assert.ThrowsAsync<PermanentFailureException>(() => Seed.ReadyAsync(t, invalid));

        Assert.Equal((0, 0, 0, 0), await CountsAsync(t, ready.OrderId));
    }
}
