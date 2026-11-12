using Bloom.Fulfillment.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Tests;

public class SchemaTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id() => Guid.CreateVersion7();

    private static PackTask NewTask(Guid? refId = null) => new()
    {
        Id = Id(),
        RefId = refId ?? Id(),
        OrderId = Id(),
        Kind = TaskKinds.Order,
        Status = TaskStatuses.Pending,
        ShipTo = """{"name":"Ann"}""",
        ShippingOption = ShippingOptions.Standard,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Shipment NewShipment(string? tracking = null, string? transaction = null) => new()
    {
        Id = Id(),
        OrderId = Id(),
        Direction = Directions.Outbound,
        Service = ShippingOptions.Standard,
        Status = ShipmentStatuses.LabelPending,
        TrackingNumber = tracking,
        ShippoTransactionId = transaction,
        NextAttemptAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static OrderLineAccount NewLine() => new()
    {
        OrderId = Id(),
        LineNo = 1,
        Kind = LineKinds.Product,
        Quantity = 2,
        PaidCents = 1001,
        TaxCents = 80,
    };

    private static ReturnRequest NewReturn() => new()
    {
        Id = Id(),
        OrderId = Id(),
        AccountId = Id(),
        Status = ReturnStatuses.Requested,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static DeliveryFailure NewFailure() => new()
    {
        ShipmentId = Id(),
        Kind = FailureKinds.Lost,
        Status = FailureStatuses.PossiblyLost,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static WarrantyCoverage NewCoverage(string serial, string status) => new()
    {
        Id = Id(),
        OrderId = Id(),
        PlanLineNo = 2,
        DeviceLineNo = 1,
        Serial = serial,
        CoverageMonths = 12,
        Status = status,
    };

    private static FulfillmentSaga NewSaga(Guid refundId) => new()
    {
        RefId = Id(),
        Type = SagaTypes.ReturnRefund,
        PendingSteps = ["refund"],
        RefundId = refundId,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<T> InsertAsync<T>(FulfillmentDbContext db, T entity) where T : notnull
    {
        db.Add(entity);
        await db.SaveChangesAsync(Ct);
        return entity;
    }

    private static async Task<string> ViolationAsync(FulfillmentDbContext db, object entity, string sqlState)
    {
        db.Add(entity);
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<Npgsql.PostgresException>(e.InnerException);
        Assert.Equal(sqlState, pg.SqlState);
        return pg.ConstraintName!;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Refund_above_paid_is_rejected()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var line = await InsertAsync(db, NewLine());

        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE order_line_accounts SET refunded_cents = paid_cents + 1 WHERE order_id = {line.OrderId}", Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.Equal("ck_order_line_accounts_refund_le_paid", e.ConstraintName);
    }

    [Theory]
    [InlineData("refunded_units = quantity + 1", "ck_order_line_accounts_units_le_quantity")]
    [InlineData("refunded_units = -1", "ck_order_line_accounts_refunded_non_negative")]
    [InlineData("refunded_cents = -1", "ck_order_line_accounts_refunded_non_negative")]
    public async Task Refund_counters_stay_within_quantity_and_zero(string set, string constraint)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var line = await InsertAsync(db, NewLine());

        await using var conn = new Npgsql.NpgsqlConnection(t.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new Npgsql.NpgsqlCommand("UPDATE order_line_accounts SET " + set + " WHERE order_id = @o", conn);
        cmd.Parameters.AddWithValue("o", line.OrderId);
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal(constraint, e.ConstraintName);
    }

    [Fact]
    public async Task Ref_id_is_unique()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var first = await InsertAsync(db, NewTask());

        await using var dup = t.NewDb();
        Assert.Equal("ux_pack_tasks_ref_id", await ViolationAsync(dup, NewTask(first.RefId), "23505"));
    }

    [Fact]
    public async Task One_open_coverage_per_serial()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        db.AddRange(NewCoverage("S1", CoverageStatuses.PendingDelivery), NewCoverage("S2", CoverageStatuses.Cancelled), NewCoverage("S2", CoverageStatuses.Cancelled));
        await db.SaveChangesAsync(Ct);
        db.Add(NewCoverage("S2", CoverageStatuses.Active)); // cancelled coverages don't count
        await db.SaveChangesAsync(Ct);

        await using var dup = t.NewDb();
        Assert.Equal("ux_warranty_coverages_serial_open", await ViolationAsync(dup, NewCoverage("S1", CoverageStatuses.PendingDelivery), "23505"));
        await using var dup2 = t.NewDb();
        Assert.Equal("ux_warranty_coverages_serial_open", await ViolationAsync(dup2, NewCoverage("S2", CoverageStatuses.PendingDelivery), "23505"));
    }

    [Fact]
    public async Task Tracking_number_and_refund_id_are_unique()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var refundId = Id();
        db.AddRange(NewShipment("TRK1", "tx1"), NewShipment(), NewShipment(), NewSaga(refundId)); // nulls never collide
        await db.SaveChangesAsync(Ct);

        await using var d1 = t.NewDb();
        Assert.Equal("ux_shipments_tracking_number", await ViolationAsync(d1, NewShipment("TRK1"), "23505"));
        await using var d2 = t.NewDb();
        Assert.Equal("ux_shipments_shippo_transaction_id", await ViolationAsync(d2, NewShipment(transaction: "tx1"), "23505"));
        await using var d3 = t.NewDb();
        Assert.Equal("ux_fulfillment_sagas_refund_id", await ViolationAsync(d3, NewSaga(refundId), "23505"));
    }

    [Fact]
    public async Task Enum_columns_reject_unknown_values()
    {
        await using var t = await TestFulfillment.StartAsync(containers);

        async Task<string> RejectAsync(object entity)
        {
            await using var db = t.NewDb();
            return await ViolationAsync(db, entity, "23514");
        }

        var shipment = NewShipment(); shipment.Status = "x";
        Assert.Equal("ck_shipments_status", await RejectAsync(shipment));
        shipment = NewShipment(); shipment.Direction = "x";
        Assert.Equal("ck_shipments_direction", await RejectAsync(shipment));
        shipment = NewShipment(); shipment.Service = "x";
        Assert.Equal("ck_shipments_service", await RejectAsync(shipment));
        var task = NewTask(); task.Status = "x";
        Assert.Equal("ck_pack_tasks_status", await RejectAsync(task));
        task = NewTask(); task.Kind = "x";
        Assert.Equal("ck_pack_tasks_kind", await RejectAsync(task));
        var request = NewReturn(); request.Status = "x";
        Assert.Equal("ck_return_requests_status", await RejectAsync(request));
        var failure = NewFailure(); failure.Status = "x";
        Assert.Equal("ck_delivery_failures_status", await RejectAsync(failure));
        failure = NewFailure(); failure.Kind = "x";
        Assert.Equal("ck_delivery_failures_kind", await RejectAsync(failure));
        failure = NewFailure(); failure.PreviousStatus = "x";
        Assert.Equal("ck_delivery_failures_previous_status", await RejectAsync(failure));
        var saga = NewSaga(Id()); saga.Type = "x";
        Assert.Equal("ck_fulfillment_sagas_type", await RejectAsync(saga));
        var coverage = NewCoverage("S9", "x");
        Assert.Equal("ck_warranty_coverages_status", await RejectAsync(coverage));
        var line = NewLine(); line.Kind = "x";
        Assert.Equal("ck_order_line_accounts_kind", await RejectAsync(line));
    }

    [Fact]
    public async Task Child_tables_and_nullable_enums_round_trip()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var task = NewTask();
        var request = NewReturn();
        db.AddRange(task, request,
            new PackTaskLine { TaskId = task.Id, LineNo = 1, Kind = LineKinds.Product, SkuId = Id(), ProductId = Id(), Serialized = true, Quantity = 1 },
            new PackTaskSerial { TaskId = task.Id, Serial = "A1", LineNo = 1, Seq = 1, ScannedAt = DateTimeOffset.UtcNow },
            new ReturnUnit { ReturnId = request.Id, UnitNo = 1, LineNo = 1, InspectionResult = InspectionResults.Ok, Decision = Decisions.Full });
        await db.SaveChangesAsync(Ct);

        await using var dup = t.NewDb();
        Assert.Equal("ux_pack_task_serials_task_seq",
            await ViolationAsync(dup, new PackTaskSerial { TaskId = task.Id, Serial = "A2", LineNo = 1, Seq = 1, ScannedAt = DateTimeOffset.UtcNow }, "23505"));
        await using var bad = t.NewDb();
        Assert.Equal("ck_return_units_decision",
            await ViolationAsync(bad, new ReturnUnit { ReturnId = request.Id, UnitNo = 2, LineNo = 1, Decision = "x" }, "23514"));
    }

    [Fact]
    public async Task Lld_indexes_exist()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var db = t.NewDb();
        var defs = await db.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync(Ct);

        // (table, definition fragments) of LLD 05-fulfillment §4 and spec §4/§7.
        (string Table, string[] Parts)[] expected =
        [
            ("shipments", ["(next_attempt_at)", "WHERE", "'label_pending'"]),
            ("shipments", ["(pack_task_id)"]),
            ("shipments", ["ix_shipments_return_request_id", "(return_request_id)"]),
            ("pack_tasks", ["ix_pack_tasks_order_id", "(order_id)"]),
            ("order_accounts", ["(account_id)"]),
            ("return_requests", ["(order_id)"]),
            ("return_requests", ["(account_id, created_at)"]),
            ("return_requests", ["(expires_at)", "WHERE", "'requested'", "'label_issued'"]),
            ("warranty_coverages", ["(order_id)"]),
        ];
        foreach (var (table, parts) in expected)
            Assert.Contains(defs, d => d.Contains($" ON public.{table} ") && parts.All(d.Contains));
    }
}
