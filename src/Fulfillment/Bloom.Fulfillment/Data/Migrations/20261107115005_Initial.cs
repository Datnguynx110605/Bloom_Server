using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Fulfillment.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_failures",
                columns: table => new
                {
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    previous_status = table.Column<string>(type: "text", nullable: true),
                    rts_outcomes = table.Column<string>(type: "jsonb", nullable: true),
                    reship_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_failures", x => x.shipment_id);
                    table.CheckConstraint("ck_delivery_failures_kind", "kind IN ('returned_to_sender', 'lost')");
                    table.CheckConstraint("ck_delivery_failures_previous_status", "previous_status IN ('label_pending', 'label_ready', 'handed_over', 'in_transit', 'delivered', 'possibly_lost', 'lost', 'returned_to_sender', 'void_pending', 'voided')");
                    table.CheckConstraint("ck_delivery_failures_status", "status IN ('possibly_lost', 'confirmed', 'resolved_refund', 'resolved_reship', 'dismissed')");
                });

            migrationBuilder.CreateTable(
                name: "fulfillment_sagas",
                columns: table => new
                {
                    ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    pending_steps = table.Column<string[]>(type: "text[]", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fulfillment_sagas", x => new { x.ref_id, x.type });
                    table.CheckConstraint("ck_fulfillment_sagas_type", "type IN ('return_refund', 'delivery_failure')");
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
                name: "order_accounts",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipping_cents = table.Column<long>(type: "bigint", nullable: false),
                    total_cents = table.Column<long>(type: "bigint", nullable: false),
                    shipping_refunded = table.Column<bool>(type: "boolean", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_accounts", x => x.order_id);
                });

            migrationBuilder.CreateTable(
                name: "order_line_accounts",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    parent_line_no = table.Column<int>(type: "integer", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    paid_cents = table.Column<long>(type: "bigint", nullable: false),
                    tax_cents = table.Column<long>(type: "bigint", nullable: false),
                    refunded_units = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    refunded_cents = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_line_accounts", x => new { x.order_id, x.line_no });
                    table.CheckConstraint("ck_order_line_accounts_kind", "kind IN ('product', 'warranty_plan')");
                    table.CheckConstraint("ck_order_line_accounts_refund_le_paid", "refunded_cents <= paid_cents");
                    table.CheckConstraint("ck_order_line_accounts_refunded_non_negative", "refunded_units >= 0 AND refunded_cents >= 0");
                    table.CheckConstraint("ck_order_line_accounts_units_le_quantity", "refunded_units <= quantity");
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
                name: "pack_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    cancelled_before_arrival = table.Column<bool>(type: "boolean", nullable: false),
                    ship_to = table.Column<string>(type: "jsonb", nullable: true),
                    shipping_option = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pack_tasks", x => x.id);
                    table.CheckConstraint("ck_pack_tasks_kind", "kind IN ('order', 'reship')");
                    table.CheckConstraint("ck_pack_tasks_shipping_option", "shipping_option IN ('standard', 'express')");
                    table.CheckConstraint("ck_pack_tasks_status", "status IN ('pending', 'packing', 'packed', 'handed_over', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "return_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    return_shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_requests", x => x.id);
                    table.CheckConstraint("ck_return_requests_status", "status IN ('requested', 'label_issued', 'in_transit', 'received', 'inspected', 'decided', 'expired')");
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    shippo_transaction_id = table.Column<string>(type: "text", nullable: true),
                    tracking_number = table.Column<string>(type: "text", nullable: true),
                    label_url = table.Column<string>(type: "text", nullable: true),
                    last_tracking_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handed_over_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    label_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_direction", "direction IN ('outbound', 'return', 'ship_back')");
                    table.CheckConstraint("ck_shipments_service", "service IN ('standard', 'express')");
                    table.CheckConstraint("ck_shipments_status", "status IN ('label_pending', 'label_ready', 'handed_over', 'in_transit', 'delivered', 'possibly_lost', 'lost', 'returned_to_sender', 'void_pending', 'voided')");
                });

            migrationBuilder.CreateTable(
                name: "warranty_coverages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_line_no = table.Column<int>(type: "integer", nullable: false),
                    device_line_no = table.Column<int>(type: "integer", nullable: false),
                    serial = table.Column<string>(type: "text", nullable: false),
                    coverage_months = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warranty_coverages", x => x.id);
                    table.CheckConstraint("ck_warranty_coverages_status", "status IN ('pending_delivery', 'active', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "pack_task_lines",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serialized = table.Column<bool>(type: "boolean", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    parent_line_no = table.Column<int>(type: "integer", nullable: true),
                    coverage_months = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pack_task_lines", x => new { x.task_id, x.line_no });
                    table.CheckConstraint("ck_pack_task_lines_kind", "kind IN ('product', 'warranty_plan')");
                    table.ForeignKey(
                        name: "FK_pack_task_lines_pack_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "pack_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pack_task_serials",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial = table.Column<string>(type: "text", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pack_task_serials", x => new { x.task_id, x.serial });
                    table.ForeignKey(
                        name: "FK_pack_task_serials_pack_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "pack_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_units",
                columns: table => new
                {
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_no = table.Column<int>(type: "integer", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    received_serial = table.Column<string>(type: "text", nullable: true),
                    inspection_result = table.Column<string>(type: "text", nullable: true),
                    inspection_notes = table.Column<string>(type: "text", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: true),
                    refund_cents = table.Column<long>(type: "bigint", nullable: false),
                    refund_tax_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_units", x => new { x.return_id, x.unit_no });
                    table.CheckConstraint("ck_return_units_decision", "decision IN ('full', 'partial', 'reject')");
                    table.CheckConstraint("ck_return_units_inspection_result", "inspection_result IN ('ok', 'damaged', 'missing_parts', 'wrong_item')");
                    table.ForeignKey(
                        name: "FK_return_units_return_requests_return_id",
                        column: x => x.return_id,
                        principalTable: "return_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_fulfillment_sagas_refund_id",
                table: "fulfillment_sagas",
                column: "refund_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                table: "idempotency_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_order_accounts_account",
                table: "order_accounts",
                column: "account_id");

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
                name: "ux_pack_task_serials_task_seq",
                table: "pack_task_serials",
                columns: new[] { "task_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pack_tasks_order_id",
                table: "pack_tasks",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_pack_tasks_ref_id",
                table: "pack_tasks",
                column: "ref_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_account_created",
                table: "return_requests",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_expiry",
                table: "return_requests",
                column: "expires_at",
                filter: "status IN ('requested', 'label_issued')");

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_order",
                table: "return_requests",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_label_due",
                table: "shipments",
                column: "next_attempt_at",
                filter: "status = 'label_pending'");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_pack_task",
                table: "shipments",
                column: "pack_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_return_request_id",
                table: "shipments",
                column: "return_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_shipments_shippo_transaction_id",
                table: "shipments",
                column: "shippo_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shipments_tracking_number",
                table: "shipments",
                column: "tracking_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warranty_coverages_order",
                table: "warranty_coverages",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_warranty_coverages_serial_open",
                table: "warranty_coverages",
                column: "serial",
                unique: true,
                filter: "status IN ('active', 'pending_delivery')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_failures");

            migrationBuilder.DropTable(
                name: "fulfillment_sagas");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "order_accounts");

            migrationBuilder.DropTable(
                name: "order_line_accounts");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "pack_task_lines");

            migrationBuilder.DropTable(
                name: "pack_task_serials");

            migrationBuilder.DropTable(
                name: "return_units");

            migrationBuilder.DropTable(
                name: "shipments");

            migrationBuilder.DropTable(
                name: "warranty_coverages");

            migrationBuilder.DropTable(
                name: "pack_tasks");

            migrationBuilder.DropTable(
                name: "return_requests");
        }
    }
}
