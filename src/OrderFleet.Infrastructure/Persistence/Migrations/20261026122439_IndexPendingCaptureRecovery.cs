using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexPendingCaptureRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_PendingReconciliation",
                table: "Payments",
                columns: new[] { "AuthorizedAt", "Id" },
                filter: "\"State\" = 'PendingReconciliation'")
                .Annotation("Npgsql:IndexInclude", new[] { "OrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_PendingReconciliation",
                table: "Payments");
        }
    }
}
