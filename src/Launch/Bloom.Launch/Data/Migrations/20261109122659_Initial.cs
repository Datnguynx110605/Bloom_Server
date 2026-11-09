using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Launch.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consumed_passes",
                columns: table => new
                {
                    pass_id = table.Column<Guid>(type: "uuid", nullable: false),
                    launch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consumed_passes", x => x.pass_id);
                });

            migrationBuilder.CreateTable(
                name: "holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    launch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pass_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holds", x => x.id);
                    table.CheckConstraint("ck_holds_status", "status IN ('held', 'sold', 'released')");
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
                name: "launches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    launch_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    release_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    admission_rate = table.Column<int>(type: "integer", nullable: false),
                    waiting_room_closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launches", x => x.id);
                    table.CheckConstraint("ck_launches_admission_rate", "admission_rate >= 1");
                    table.CheckConstraint("ck_launches_release_after_launch", "release_date > launch_time");
                    table.CheckConstraint("ck_launches_stage", "stage IN ('draft', 'announced', 'preorder_open', 'released')");
                });

            migrationBuilder.CreateTable(
                name: "limit_usage",
                columns: table => new
                {
                    launch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    held = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    sold = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_limit_usage", x => new { x.launch_id, x.account_id, x.product_id });
                    table.CheckConstraint("ck_limit_usage_non_negative", "held >= 0 AND sold >= 0");
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
                name: "hold_lines",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hold_lines", x => new { x.hold_id, x.sku_id });
                    table.CheckConstraint("ck_hold_lines_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_hold_lines_holds_hold_id",
                        column: x => x.hold_id,
                        principalTable: "holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "launch_products",
                columns: table => new
                {
                    launch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_limit = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launch_products", x => new { x.launch_id, x.product_id });
                    table.CheckConstraint("ck_launch_products_limit_positive", "purchase_limit > 0");
                    table.ForeignKey(
                        name: "FK_launch_products_launches_launch_id",
                        column: x => x.launch_id,
                        principalTable: "launches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "launch_skus",
                columns: table => new
                {
                    launch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation = table.Column<int>(type: "integer", nullable: false),
                    held = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    sold = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launch_skus", x => new { x.launch_id, x.sku_id });
                    table.CheckConstraint("ck_launch_skus_non_negative", "allocation >= 0 AND held >= 0 AND sold >= 0");
                    table.CheckConstraint("ck_launch_skus_within_allocation", "held + sold <= allocation");
                    table.ForeignKey(
                        name: "FK_launch_skus_launch_products_launch_id_product_id",
                        columns: x => new { x.launch_id, x.product_id },
                        principalTable: "launch_products",
                        principalColumns: new[] { "launch_id", "product_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holds_held_created_at",
                table: "holds",
                columns: new[] { "status", "created_at" },
                filter: "status = 'held'");

            migrationBuilder.CreateIndex(
                name: "ux_holds_order_id",
                table: "holds",
                column: "order_id",
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
                name: "ux_launch_products_product_id",
                table: "launch_products",
                column: "product_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_launch_skus_launch_id_product_id",
                table: "launch_skus",
                columns: new[] { "launch_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_launch_skus_sku_id",
                table: "launch_skus",
                column: "sku_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_launches_stage",
                table: "launches",
                column: "stage");

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
                name: "consumed_passes");

            migrationBuilder.DropTable(
                name: "hold_lines");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "launch_skus");

            migrationBuilder.DropTable(
                name: "limit_usage");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "holds");

            migrationBuilder.DropTable(
                name: "launch_products");

            migrationBuilder.DropTable(
                name: "launches");
        }
    }
}
