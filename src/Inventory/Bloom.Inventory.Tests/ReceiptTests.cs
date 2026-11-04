using System.Net;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class ReceiptTests(Containers containers)
{
    private const string Path = "/api/admin/inventory/receipts";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostAsync(TestInventory t, object body, TestCaller? caller = null, Guid? key = null) =>
        t.SendAsync(HttpMethod.Post, Path, body, caller ?? TestCaller.Staff("warehouse"), key ?? Guid.NewGuid());

    private static object Line(Guid skuId, int quantity, string[]? serials = null, string[]? imeis = null) =>
        new { skuId, quantity, serials = serials ?? [], imeis = imeis ?? [] };

    private static async Task<string[]> ViolationFieldsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("details").EnumerateArray()
            .Single(d => d.GetProperty("@type").GetString() == "type.googleapis.com/google.rpc.BadRequest")
            .GetProperty("fieldViolations").EnumerateArray().Select(v => v.GetProperty("field").GetString()!).ToArray();
    }

    private static async Task ExpectInvalidAsync(HttpResponseMessage response, params string[] fields)
    {
        await response.ExpectErrorAsync(400, "RECEIPT_INVALID");
        Assert.Equal(fields, await ViolationFieldsAsync(response));
    }

    private static async Task<int> KeysAsync(TestInventory t)
    {
        await using var db = t.NewDb();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM idempotency_keys").SingleAsync(Ct);
    }

    /// <summary>No row of any kind: the receipt was refused before it wrote anything, so its key is free too.</summary>
    private static async Task AssertNothingWrittenAsync(TestInventory t)
    {
        await using var db = t.NewDb();
        Assert.Empty(await db.StockItems.ToListAsync(Ct));
        Assert.Empty(await db.GoodsReceipts.ToListAsync(Ct));
        Assert.Empty(await db.GoodsReceiptLines.ToListAsync(Ct));
        Assert.Empty(await db.SerialUnits.ToListAsync(Ct));
        Assert.Empty(await db.StockLedger.ToListAsync(Ct));
        Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(Ct));
        Assert.Equal(0, await KeysAsync(t));
    }

    [Fact]
    public async Task Receipt_adds_stock_ledger_and_audit()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var staff = TestCaller.Staff("warehouse");
        var plain = Seed.Sku(t);
        var phone = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        t.Catalog.AddSku(phone, product, serialized: true);

        using var response = await PostAsync(t, new
        {
            note = "Delivery 42",
            lines = new[] { Line(plain, 5), Line(phone, 2, ["SN1", "SN2"], [Seed.Imei(1), Seed.Imei(2)]) },
        }, staff);
        var receiptId = Guid.Parse((await response.OkJsonAsync()).GetProperty("receiptId").GetString()!);

        Assert.Equal(5, (await Seed.ItemAsync(t, plain))!.OnHand);
        var item = (await Seed.ItemAsync(t, phone))!;
        Assert.Equal((2, product, true), (item.OnHand, item.ProductId, item.Serialized));
        await using var db = t.NewDb();
        var serials = await db.SerialUnits.Where(u => u.SkuId == phone).OrderBy(u => u.Serial).ToListAsync(Ct);
        Assert.Equal(["SN1", "SN2"], serials.Select(s => s.Serial));
        Assert.Equal([Seed.Imei(1), Seed.Imei(2)], serials.Select(s => s.Imei));
        Assert.All(serials, s => Assert.Equal((SerialStatuses.InStock, receiptId), (s.Status, s.ReceiptId)));
        var receipt = await db.GoodsReceipts.SingleAsync(Ct);
        Assert.Equal((receiptId, staff.Id, "Delivery 42"), (receipt.Id, receipt.StaffId, receipt.Note));
        Assert.Equal(new Dictionary<Guid, int> { [plain] = 5, [phone] = 2 }, (await db.GoodsReceiptLines.ToListAsync(Ct)).ToDictionary(l => l.SkuId, l => l.Quantity));

        var entry = Assert.Single(await Seed.LedgerAsync(t, plain));
        Assert.Equal(("received", 5, "receipt", receiptId, staff.Id.ToString("D")), (entry.EntryType, entry.DOnHand, entry.RefType, entry.RefId, entry.Actor));
        Assert.Equal(0, entry.DReserved + entry.DCommitted + entry.DReturned + entry.DDamaged + entry.DLost);
        Assert.Equal(2, (await Seed.LedgerAsync(t, phone)).Single().DOnHand);
        Assert.Equal(2, metrics.Sum("ledger_entries_total", ("entry_type", "received")));

        var audit = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal((staff.Id, "warehouse", "inventory", "inventory.goods-received", "receipt", receiptId.ToString()),
            (audit.ActorId, audit.ActorRole, audit.Service, audit.Action, audit.TargetType, audit.TargetId));
        Assert.Equal(new[] { plain.ToString(), phone.ToString() }.Order(), audit.Details.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("skuId").GetString()).Order());
    }

    [Fact]
    public async Task I04_receipt_serves_oldest_backorders_first()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var sku = Seed.Sku(t);
        var paid = t.Clock.GetUtcNow().AddHours(-1);
        var second = await Seed.BackorderAsync(t, sku, 2, paid.AddMinutes(1)); // inserted out of order on purpose
        var first = await Seed.BackorderAsync(t, sku, 2, paid);
        var third = await Seed.BackorderAsync(t, sku, 3, paid.AddMinutes(2));

        await Seed.ReceiveAsync(t, sku, 5);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((1, 4), (item.OnHand, item.Committed));
        await using var db = t.NewDb();
        var status = await db.StockAllocations.ToDictionaryAsync(a => a.OrderId, a => a.Status, Ct);
        Assert.Equal(("committed", "committed", "backordered"), (status[first], status[second], status[third]));
        var allocated = (await Seed.LedgerAsync(t, sku)).Where(e => e.EntryType == "backorder_allocated").ToList();
        Assert.Equal(new[] { first, second }.Order(), allocated.Select(e => e.RefId).Order());
        Assert.All(allocated, e => Assert.Equal(("order", -2, 2), (e.RefType, e.DOnHand, e.DCommitted)));
        Assert.Equal(new[] { first, second }.Order(), (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId).Order());
        Assert.Equal(2, metrics.Count("backorder_wait_seconds"));
    }

    [Fact]
    public async Task Parallel_receipts_for_the_lines_of_one_preorder_announce_it_once()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var paidAt = DateTimeOffset.UtcNow;
        var orders = new List<Guid>();
        for (var round = 0; round < 20; round++)
        {
            var receipts = new List<Task>();
            for (var i = 0; i < 4; i++)
            {
                var (a, b) = (Seed.Sku(t), Seed.Sku(t));
                var orderId = await Seed.BackorderAsync(t, a, 1, paidAt);
                await Seed.BackorderAsync(t, b, 1, paidAt, orderId);
                orders.Add(orderId);
                receipts.Add(Seed.ReceiveAsync(t, a, 1));
                receipts.Add(Seed.ReceiveAsync(t, b, 1));
            }
            await Task.WhenAll(receipts);
        }

        await using var db = t.NewDb();
        Assert.All(await db.StockAllocations.AsNoTracking().ToListAsync(Ct), a => Assert.Equal(AllocationStatuses.Committed, a.Status));
        Assert.Equal(orders.Order(), (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId).Order());
    }

    [Fact]
    public async Task I05_receipt_smaller_than_oldest_backorder_allocates_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var paid = t.Clock.GetUtcNow().AddHours(-1);
        await Seed.BackorderAsync(t, sku, 2, paid);
        await Seed.BackorderAsync(t, sku, 1, paid.AddMinutes(1)); // would fit, but is behind the oldest

        await Seed.ReceiveAsync(t, sku, 1);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((1, 0), (item.OnHand, item.Committed));
        await using var db = t.NewDb();
        Assert.All(await db.StockAllocations.ToListAsync(Ct), a => Assert.Equal("backordered", a.Status));
        Assert.Empty(await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated));
    }

    [Fact]
    public async Task I07_one_duplicate_serial_among_100_rejects_the_whole_receipt()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        var serials = Enumerable.Range(0, 99).Select(i => $"SN{i:D3}").Append("SN005").ToArray();

        using var response = await PostAsync(t, new { lines = new[] { Line(sku, 100, serials) } });

        await ExpectInvalidAsync(response, "lines[0].serials[99]");
        Assert.Equal(0, t.Catalog.LockCalls(sku));
        await AssertNothingWrittenAsync(t);
    }

    [Fact]
    public async Task Every_bad_line_is_listed()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var held = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, held, 1, ["HELD1"]);
        var (a, b, c) = (Seed.Sku(t), Seed.Sku(t, serialized: true), Seed.Sku(t, serialized: true));

        using var response = await PostAsync(t, new
        {
            lines = new[]
            {
                Line(a, 0),                                                    // quantity
                Line(held, 1, ["held1"]),                                      // already in stock, after normalization
                Line(b, 1, ["SN2"], ["490154203237519"]),                      // IMEI fails Luhn
                Line(c, 2, ["SN3", "SN4"], [Seed.Imei(3)]),                         // IMEI count differs
                Line(a, 1),                                                    // duplicate sku line
            },
        });

        await ExpectInvalidAsync(response, "lines[0].quantity", "lines[1].serials[0]", "lines[2].imeis[0]", "lines[3].imeis", "lines[4].sku_id");
    }

    [Fact]
    public async Task Serial_count_and_non_serialized_checks_run_after_the_lock()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var phone = Seed.Sku(t, serialized: true);
        var plain = Seed.Sku(t);

        using var response = await PostAsync(t, new { lines = new[] { Line(phone, 3, ["SN1", "SN2"]), Line(plain, 1, ["SN9"]) } });

        await ExpectInvalidAsync(response, "lines[0].serials", "lines[1].serials");
        Assert.Equal((1, 1), (t.Catalog.LockCalls(phone), t.Catalog.LockCalls(plain))); // accepted: spec §4
        await AssertNothingWrittenAsync(t);
    }

    [Fact]
    public async Task Format_errors_skip_the_catalog_call()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);

        using var response = await PostAsync(t, new { lines = new[] { Line(sku, 1, ["not valid!"]) } });

        await ExpectInvalidAsync(response, "lines[0].serials[0]");
        Assert.Equal(0, t.Catalog.LockCalls(sku));
    }

    [Fact]
    public async Task I08_catalog_lock_called_on_first_receipt_only()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);

        await Seed.ReceiveAsync(t, sku, 1);
        await Seed.ReceiveAsync(t, sku, 1);

        Assert.Equal(1, t.Catalog.LockCalls(sku));
        Assert.Equal(2, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Unknown_sku_is_listed()
    {
        await using var t = await TestInventory.StartAsync(containers);

        using var response = await PostAsync(t, new { lines = new[] { Line(Guid.NewGuid(), 1) } });

        await ExpectInvalidAsync(response, "lines[0].sku_id");
        await AssertNothingWrittenAsync(t);
    }

    [Fact]
    public async Task Catalog_down_on_first_receipt_writes_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var (staff, key) = (TestCaller.Staff("warehouse"), Guid.NewGuid());
        var body = new { lines = new[] { Line(sku, 3) } };
        t.Catalog.Down = true;

        using var down = await PostAsync(t, body, staff, key);

        await down.ExpectErrorAsync(503, "CATALOG_UNAVAILABLE");
        await AssertNothingWrittenAsync(t);

        t.Catalog.Down = false;
        using var up = await PostAsync(t, body, staff, key);
        await up.OkJsonAsync();
        Assert.Equal(3, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Serials_are_trimmed_and_upper_cased()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);

        await Seed.ReceiveAsync(t, sku, 1, [" abc123 "]);

        await using var db = t.NewDb();
        Assert.Equal("ABC123", (await db.SerialUnits.SingleAsync(Ct)).Serial);

        using var response = await PostAsync(t, new { lines = new[] { Line(sku, 2, ["abc1", "ABC1"]) } });
        await ExpectInvalidAsync(response, "lines[0].serials[1]");
    }

    [Fact]
    public async Task Same_key_replays_the_receipt()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var (staff, key) = (TestCaller.Staff("warehouse"), Guid.NewGuid());
        var body = new { lines = new[] { Line(sku, 5) } };

        using var first = await PostAsync(t, body, staff, key);
        var receiptId = (await first.OkJsonAsync()).GetProperty("receiptId").GetString();
        using var again = await PostAsync(t, body, staff, key);

        Assert.Equal(receiptId, (await again.OkJsonAsync()).GetProperty("receiptId").GetString());
        Assert.Equal(5, (await Seed.ItemAsync(t, sku))!.OnHand);
        using var different = await PostAsync(t, new { lines = new[] { Line(sku, 6) } }, staff, key);
        await different.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REUSED");
        using var missing = await t.SendAsync(HttpMethod.Post, Path, body, staff);
        await missing.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Equal(5, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Same_key_replays_a_serialized_receipt()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        var (staff, key) = (TestCaller.Staff("warehouse"), Guid.NewGuid());
        var body = new { lines = new[] { Line(sku, 2, ["SN1", "SN2"], [Seed.Imei(1), Seed.Imei(2)]) } };

        using var first = await PostAsync(t, body, staff, key);
        var receiptId = (await first.OkJsonAsync()).GetProperty("receiptId").GetString();
        using var again = await PostAsync(t, body, staff, key); // its own serials are stored now; the retry must not trip over them

        Assert.Equal(receiptId, (await again.OkJsonAsync()).GetProperty("receiptId").GetString());
        Assert.Equal(2, (await Seed.ItemAsync(t, sku))!.OnHand);
        await using var db = t.NewDb();
        var serials = await db.SerialUnits.ToListAsync(Ct);
        Assert.Equal(["SN1", "SN2"], serials.Select(s => s.Serial).Order());
        Assert.All(serials, s => Assert.Equal(receiptId, s.ReceiptId.ToString()));
        Assert.Single(await Outbox.AuditAsync(t));
        using var different = await PostAsync(t, new { lines = new[] { Line(sku, 1, ["SN3"]) } }, staff, key);
        await different.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task Concurrent_receipts_for_one_sku_allocate_serially()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var paid = t.Clock.GetUtcNow().AddHours(-1);
        var orders = new List<Guid>();
        foreach (var i in Enumerable.Range(0, 10)) orders.Add(await Seed.BackorderAsync(t, sku, 1, paid.AddMinutes(i)));

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Seed.ReceiveAsync(t, sku, 1)));

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((0, 10), (item.OnHand, item.Committed));
        await using var db = t.NewDb();
        Assert.All(await db.StockAllocations.ToListAsync(Ct), a => Assert.Equal("committed", a.Status));
        // The row lock serializes the transactions, so physical insert order is allocation order.
        var served = await db.Database.SqlQuery<Guid>($"SELECT ref_id AS \"Value\" FROM stock_ledger WHERE sku_id = {sku} AND entry_type = 'backorder_allocated' ORDER BY ctid").ToListAsync(Ct);
        Assert.Equal(orders, served);
    }

    [Fact]
    public async Task Racing_receipts_of_one_serial_leave_exactly_one()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        var body = new { lines = new[] { Line(sku, 1, ["RACE1"]) } };

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(t, body)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.OK)) await lost.ExpectErrorAsync(400, "RECEIPT_INVALID");
        Assert.Equal(1, (await Seed.ItemAsync(t, sku))!.OnHand);
        await using var db = t.NewDb();
        Assert.Single(await db.GoodsReceipts.ToListAsync(Ct));
    }

    [Theory, InlineData("support"), InlineData("catalog_manager")]
    public async Task Only_warehouse_and_admin_record_receipts(string role)
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);

        using var response = await PostAsync(t, new { lines = new[] { Line(sku, 1) } }, TestCaller.Staff(role));

        await response.ExpectErrorAsync((int)HttpStatusCode.Forbidden, "ROLE_NOT_ALLOWED");
        await AssertNothingWrittenAsync(t);
        Assert.Equal(0, t.Catalog.LockCalls(sku));
    }
}
