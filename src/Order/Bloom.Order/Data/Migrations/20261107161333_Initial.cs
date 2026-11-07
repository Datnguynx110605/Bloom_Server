using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bloom.Order.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carts",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carts", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "checkout_settings",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    express_fee_cents = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_settings", x => x.id);
                    table.CheckConstraint("ck_checkout_settings_express_fee_non_negative", "express_fee_cents >= 0");
                    table.CheckConstraint("ck_checkout_settings_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_normalized = table.Column<string>(type: "text", nullable: false),
                    code_display = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    min_spend_cents = table.Column<long>(type: "bigint", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_limit = table.Column<int>(type: "integer", nullable: true),
                    per_customer_limit = table.Column<int>(type: "integer", nullable: true),
                    uses_count = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupons", x => x.id);
                    table.CheckConstraint("ck_coupons_scope", "scope IN ('order', 'selected')");
                    table.CheckConstraint("ck_coupons_type", "type IN ('percent', 'fixed')");
                    table.CheckConstraint("ck_coupons_uses_le_total_limit", "total_limit IS NULL OR uses_count <= total_limit");
                    table.CheckConstraint("ck_coupons_uses_non_negative", "uses_count >= 0");
                    table.CheckConstraint("ck_coupons_value_by_type", "(type = 'percent' AND value BETWEEN 1 AND 100) OR (type = 'fixed' AND value > 0)");
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
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    launch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    stock_allocated = table.Column<bool>(type: "boolean", nullable: false),
                    launch_released = table.Column<bool>(type: "boolean", nullable: false),
                    handed_to_fulfillment = table.Column<bool>(type: "boolean", nullable: false),
                    open_returns = table.Column<int>(type: "integer", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipping_option = table.Column<string>(type: "text", nullable: false),
                    ship_to = table.Column<string>(type: "jsonb", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    subtotal_cents = table.Column<long>(type: "bigint", nullable: false),
                    discount_cents = table.Column<long>(type: "bigint", nullable: false),
                    tax_cents = table.Column<long>(type: "bigint", nullable: false),
                    shipping_cents = table.Column<long>(type: "bigint", nullable: false),
                    total_cents = table.Column<long>(type: "bigint", nullable: false),
                    tax_rate_bp = table.Column<int>(type: "integer", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_amounts_non_negative", "subtotal_cents >= 0 AND discount_cents >= 0 AND tax_cents >= 0 AND shipping_cents >= 0 AND total_cents >= 0");
                    table.CheckConstraint("ck_orders_cancel_reason", "cancel_reason IN ('checkout_failed', 'checkout_expired', 'customer', 'support')");
                    table.CheckConstraint("ck_orders_kind", "kind IN ('regular', 'launch')");
                    table.CheckConstraint("ck_orders_open_returns_non_negative", "open_returns >= 0");
                    table.CheckConstraint("ck_orders_shipping_option", "shipping_option IN ('standard', 'express')");
                    table.CheckConstraint("ck_orders_status", "status IN ('creating', 'pending_payment', 'paid', 'awaiting_stock', 'packing', 'shipped', 'delivered', 'cancelled', 'delivery_failed', 'partially_returned', 'returned')");
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
                name: "cart_lines",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_line_no = table.Column<int>(type: "integer", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cart_lines", x => new { x.account_id, x.line_no });
                    table.CheckConstraint("ck_cart_lines_kind", "kind IN ('product', 'warranty_plan')");
                    table.CheckConstraint("ck_cart_lines_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_cart_lines_carts_account_id",
                        column: x => x.account_id,
                        principalTable: "carts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coupon_customer_usage",
                columns: table => new
                {
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uses_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_customer_usage", x => new { x.coupon_id, x.account_id });
                    table.CheckConstraint("ck_coupon_customer_usage_uses_non_negative", "uses_count >= 0");
                    table.ForeignKey(
                        name: "FK_coupon_customer_usage_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coupon_scope_items",
                columns: table => new
                {
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_scope_items", x => new { x.coupon_id, x.target_type, x.target_id });
                    table.CheckConstraint("ck_coupon_scope_items_target_type", "target_type IN ('product', 'category')");
                    table.ForeignKey(
                        name: "FK_coupon_scope_items_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkout_keys",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_keys", x => new { x.account_id, x.idempotency_key });
                    table.ForeignKey(
                        name: "FK_checkout_keys_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coupon_uses",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_uses", x => x.order_id);
                    table.CheckConstraint("ck_coupon_uses_status", "status IN ('held', 'consumed', 'released', 'restored')");
                    table.ForeignKey(
                        name: "FK_coupon_uses_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coupon_uses_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    serialized = table.Column<bool>(type: "boolean", nullable: false),
                    parent_line_no = table.Column<int>(type: "integer", nullable: true),
                    coverage_months = table.Column<int>(type: "integer", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    discount_cents = table.Column<long>(type: "bigint", nullable: false),
                    tax_cents = table.Column<long>(type: "bigint", nullable: false),
                    paid_cents = table.Column<long>(type: "bigint", nullable: false),
                    returned_qty = table.Column<int>(type: "integer", nullable: false),
                    refunded_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => new { x.order_id, x.line_no });
                    table.CheckConstraint("ck_order_lines_kind", "kind IN ('product', 'warranty_plan')");
                    table.CheckConstraint("ck_order_lines_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_order_lines_refund_le_paid", "refunded_cents <= paid_cents");
                    table.CheckConstraint("ck_order_lines_refunded_non_negative", "refunded_cents >= 0");
                    table.CheckConstraint("ck_order_lines_returned_qty_in_range", "returned_qty BETWEEN 0 AND quantity");
                    table.ForeignKey(
                        name: "FK_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_sagas",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    pending_steps = table.Column<string[]>(type: "text[]", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_sagas", x => new { x.order_id, x.type });
                    table.CheckConstraint("ck_order_sagas_pending_steps", "pending_steps <@ ARRAY['inventory_release', 'launch_release', 'refund', 'refund_failed']::text[]");
                    table.CheckConstraint("ck_order_sagas_type", "type IN ('checkout_compensation', 'expiry', 'cancellation', 'late_payment')");
                    table.ForeignKey(
                        name: "FK_order_sagas_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_type", "actor_type IN ('customer', 'staff', 'system')");
                    table.CheckConstraint("ck_order_status_history_from_status", "from_status IN ('creating', 'pending_payment', 'paid', 'awaiting_stock', 'packing', 'shipped', 'delivered', 'cancelled', 'delivery_failed', 'partially_returned', 'returned')");
                    table.CheckConstraint("ck_order_status_history_to_status", "to_status IN ('creating', 'pending_payment', 'paid', 'awaiting_stock', 'packing', 'shipped', 'delivered', 'cancelled', 'delivery_failed', 'partially_returned', 'returned')");
                    table.ForeignKey(
                        name: "FK_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "checkout_settings",
                columns: new[] { "id", "express_fee_cents", "updated_at", "updated_by", "version" },
                values: new object[] { (short)1, 1500L, new DateTimeOffset(new DateTime(2026, 11, 7, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, 0L });

            migrationBuilder.CreateIndex(
                name: "ux_checkout_keys_order_id",
                table: "checkout_keys",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coupon_uses_coupon_id",
                table: "coupon_uses",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ux_coupons_code_normalized",
                table: "coupons",
                column: "code_normalized",
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
                name: "ix_order_status_history_order_at",
                table: "order_status_history",
                columns: new[] { "order_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_created",
                table: "orders",
                columns: new[] { "account_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_launch_status",
                table: "orders",
                columns: new[] { "launch_id", "status" },
                filter: "kind = 'launch'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_sweeps",
                table: "orders",
                columns: new[] { "status", "deadline_at" },
                filter: "status IN ('creating', 'pending_payment')");

            migrationBuilder.CreateIndex(
                name: "ux_orders_number",
                table: "orders",
                column: "number",
                unique: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cart_lines");

            migrationBuilder.DropTable(
                name: "checkout_keys");

            migrationBuilder.DropTable(
                name: "checkout_settings");

            migrationBuilder.DropTable(
                name: "coupon_customer_usage");

            migrationBuilder.DropTable(
                name: "coupon_scope_items");

            migrationBuilder.DropTable(
                name: "coupon_uses");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "order_sagas");

            migrationBuilder.DropTable(
                name: "order_status_history");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "carts");

            migrationBuilder.DropTable(
                name: "coupons");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
