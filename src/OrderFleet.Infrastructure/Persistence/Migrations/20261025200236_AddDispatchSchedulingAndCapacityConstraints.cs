using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchSchedulingAndCapacityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DispatchAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CourierId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchAttempts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DispatchJobs",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchJobs", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_DispatchJobs_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AssignmentId",
                table: "Orders",
                column: "AssignmentId",
                unique: true,
                filter: "\"AssignmentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CourierId",
                table: "Orders",
                column: "CourierId",
                unique: true,
                filter: "\"CourierId\" IS NOT NULL AND \"Status\" IN ('Assigned', 'PickedUp')");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchAttempts_OrderId_OccurredAtUtc",
                table: "DispatchAttempts",
                columns: new[] { "OrderId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJobs_Status_NextAttemptAtUtc",
                table: "DispatchJobs",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchAttempts");

            migrationBuilder.DropTable(
                name: "DispatchJobs");

            migrationBuilder.DropIndex(
                name: "IX_Orders_AssignmentId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CourierId",
                table: "Orders");
        }
    }
}
