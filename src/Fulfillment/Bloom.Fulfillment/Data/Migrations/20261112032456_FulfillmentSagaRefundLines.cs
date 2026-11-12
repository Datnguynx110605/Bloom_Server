using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Fulfillment.Data.Migrations
{
    /// <inheritdoc />
    public partial class FulfillmentSagaRefundLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refund_lines",
                table: "fulfillment_sagas",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "refunded_shipping",
                table: "fulfillment_sagas",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_lines",
                table: "fulfillment_sagas");

            migrationBuilder.DropColumn(
                name: "refunded_shipping",
                table: "fulfillment_sagas");
        }
    }
}
