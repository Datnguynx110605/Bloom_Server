using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Notification.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contacts",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "email_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_email = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provider_message_id = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_jobs", x => x.id);
                    table.CheckConstraint("ck_email_jobs_attempts_non_negative", "attempts >= 0");
                    table.CheckConstraint("ck_email_jobs_status", "status IN ('pending', 'sent', 'failed', 'cancelled')");
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
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    link = table.Column<string>(type: "text", nullable: false),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_refs",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_refs", x => x.order_id);
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

            migrationBuilder.CreateIndex(
                name: "ix_email_jobs_status_next_attempt_at",
                table: "email_jobs",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_email_jobs_source_message_id_template",
                table: "email_jobs",
                columns: new[] { "source_message_id", "template" },
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
                name: "ix_notifications_account_id_created_at",
                table: "notifications",
                columns: new[] { "account_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_account_id_unread",
                table: "notifications",
                column: "account_id",
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_source_message_id_type",
                table: "notifications",
                columns: new[] { "source_message_id", "type" },
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
                name: "contacts");

            migrationBuilder.DropTable(
                name: "email_jobs");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "order_refs");

            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
