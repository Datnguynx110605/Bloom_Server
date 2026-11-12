using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Order.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_open_returns_non_negative",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "open_returns",
                table: "orders");

            migrationBuilder.CreateTable(
                name: "order_returns",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_returns", x => new { x.order_id, x.return_id });
                    table.ForeignKey(
                        name: "FK_order_returns_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_returns");

            migrationBuilder.AddColumn<int>(
                name: "open_returns",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_open_returns_non_negative",
                table: "orders",
                sql: "open_returns >= 0");
        }
    }
}
