using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BoundAssignmentRecoveryCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignmentCount",
                table: "DispatchJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.Sql("""
                UPDATE "DispatchJobs" job SET "AssignmentCount" = (
                    SELECT count(*)::integer FROM "DispatchAttempts" attempt
                    WHERE attempt."OrderId" = job."OrderId" AND attempt."Outcome" = 'Assigned')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignmentCount",
                table: "DispatchJobs");
        }
    }
}
