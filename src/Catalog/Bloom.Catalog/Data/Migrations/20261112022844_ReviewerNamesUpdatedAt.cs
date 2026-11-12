using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bloom.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewerNamesUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "reviewer_names",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero)); // D37: existing rows get '1970-01-01Z', older than any event

            migrationBuilder.Sql("ALTER TABLE reviewer_names ALTER COLUMN updated_at DROP DEFAULT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "reviewer_names");
        }
    }
}
