using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "goods_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => new { x.user_id, x.key });
                    table.CheckConstraint("ck_idempotency_keys_status", "status IN ('in_progress', 'completed')");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    exchange = table.Column<string>(type: "text", nullable: false),
                    routing_key = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_kind", "kind IN ('event', 'command')");
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('pending', 'sent')");
                });

            migrationBuilder.CreateTable(
                name: "stock_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial = table.Column<string>(type: "text", nullable: true),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    staff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_adjustments", x => x.id);
                    table.CheckConstraint("ck_stock_adjustments_delta", "delta <> 0");
                    table.CheckConstraint("ck_stock_adjustments_reason_code", "reason_code IN ('damaged_in_warehouse', 'lost_in_warehouse', 'found', 'count_correction', 'other')");
                });

            migrationBuilder.CreateTable(
                name: "stock_allocations",
                columns: table => new
                {
                    ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_allocations", x => new { x.ref_id, x.sku_id });
                    table.CheckConstraint("ck_stock_allocations_kind", "kind IN ('regular', 'preorder', 'reship')");
                    table.CheckConstraint("ck_stock_allocations_quantity", "quantity > 0");
                    table.CheckConstraint("ck_stock_allocations_status", "status IN ('reserved', 'backordered', 'committed', 'shipped', 'released')");
                });

            migrationBuilder.CreateTable(
                name: "stock_items",
                columns: table => new
                {
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serialized = table.Column<bool>(type: "boolean", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false),
                    committed = table.Column<int>(type: "integer", nullable: false),
                    returned = table.Column<int>(type: "integer", nullable: false),
                    damaged = table.Column<int>(type: "integer", nullable: false),
                    lost = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_items", x => x.sku_id);
                    table.CheckConstraint("ck_stock_items_committed_non_negative", "committed >= 0");
                    table.CheckConstraint("ck_stock_items_damaged_non_negative", "damaged >= 0");
                    table.CheckConstraint("ck_stock_items_lost_non_negative", "lost >= 0");
                    table.CheckConstraint("ck_stock_items_on_hand_non_negative", "on_hand >= 0");
                    table.CheckConstraint("ck_stock_items_reserved_le_on_hand", "reserved <= on_hand");
                    table.CheckConstraint("ck_stock_items_reserved_non_negative", "reserved >= 0");
                    table.CheckConstraint("ck_stock_items_returned_non_negative", "returned >= 0");
                });

            migrationBuilder.CreateTable(
                name: "stock_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: false),
                    d_on_hand = table.Column<int>(type: "integer", nullable: false),
                    d_reserved = table.Column<int>(type: "integer", nullable: false),
                    d_committed = table.Column<int>(type: "integer", nullable: false),
                    d_returned = table.Column<int>(type: "integer", nullable: false),
                    d_damaged = table.Column<int>(type: "integer", nullable: false),
                    d_lost = table.Column<int>(type: "integer", nullable: false),
                    ref_type = table.Column<string>(type: "text", nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_ledger", x => x.id);
                    table.CheckConstraint("ck_stock_ledger_entry_type", "entry_type IN ('received', 'reserved', 'reservation_released', 'committed', 'backorder_allocated', 'commitment_released', 'shipped', 'customer_returned', 'rts_restocked', 'rts_damaged', 'lost_in_transit', 'adjusted')");
                    table.CheckConstraint("ck_stock_ledger_ref_type", "ref_type IN ('order', 'reship', 'receipt', 'adjustment')");
                });

            migrationBuilder.CreateTable(
                name: "goods_receipt_lines",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipt_lines", x => new { x.receipt_id, x.sku_id });
                    table.CheckConstraint("ck_goods_receipt_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_goods_receipt_lines_goods_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "goods_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "serial_units",
                columns: table => new
                {
                    serial = table.Column<string>(type: "text", nullable: false),
                    imei = table.Column<string>(type: "text", nullable: true),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serial_units", x => x.serial);
                    table.CheckConstraint("ck_serial_units_status", "status IN ('in_stock', 'assigned', 'shipped', 'returned', 'damaged', 'lost')");
                    table.ForeignKey(
                        name: "FK_serial_units_stock_items_sku_id",
                        column: x => x.sku_id,
                        principalTable: "stock_items",
                        principalColumn: "sku_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                table: "idempotency_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "id",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_sent_at",
                table: "outbox_messages",
                column: "sent_at",
                filter: "status = 'sent'");

            migrationBuilder.CreateIndex(
                name: "ix_serial_units_ref",
                table: "serial_units",
                column: "ref_id",
                filter: "ref_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_serial_units_sku_status",
                table: "serial_units",
                columns: new[] { "sku_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_serial_units_imei",
                table: "serial_units",
                column: "imei",
                unique: true,
                filter: "imei IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_allocations_fifo",
                table: "stock_allocations",
                columns: new[] { "sku_id", "paid_at", "order_id" },
                filter: "status = 'backordered'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_allocations_order",
                table: "stock_allocations",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_allocations_stuck",
                table: "stock_allocations",
                column: "created_at",
                filter: "status = 'reserved'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_sku",
                table: "stock_ledger",
                columns: new[] { "sku_id", "created_at", "id" });

            // Plan R8 (spec §4): the app role gets full DML, except that stock_ledger is append-only (IR-03).
            // The role (default inventory_app) is named by the migration connection: Options=-c inventory.app_role=<name>.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE app_role text := coalesce(nullif(current_setting('inventory.app_role', true), ''), 'inventory_app');
                BEGIN
                    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', app_role);
                    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', app_role);
                    EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON stock_ledger FROM %I', app_role);
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "goods_receipt_lines");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "serial_units");

            migrationBuilder.DropTable(
                name: "stock_adjustments");

            migrationBuilder.DropTable(
                name: "stock_allocations");

            migrationBuilder.DropTable(
                name: "stock_ledger");

            migrationBuilder.DropTable(
                name: "goods_receipts");

            migrationBuilder.DropTable(
                name: "stock_items");
        }
    }
}
