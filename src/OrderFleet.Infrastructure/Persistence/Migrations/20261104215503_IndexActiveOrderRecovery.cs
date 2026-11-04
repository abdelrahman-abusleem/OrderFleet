using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexActiveOrderRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_AcceptedRecovery",
                table: "Orders",
                columns: new[] { "AcceptedAt", "Id" },
                filter: "\"Status\" = 'Accepted'");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedRecovery",
                table: "Orders",
                column: "Id",
                filter: "\"Status\" = 'Created'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_AcceptedRecovery",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedRecovery",
                table: "Orders");
        }
    }
}
