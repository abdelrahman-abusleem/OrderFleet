using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistCourierAssignmentDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CourierAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourierId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PickupDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PickedUpAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourierAssignments", x => x.Id);
                    table.CheckConstraint("CK_Assignment_Deadline", "\"PickupDeadlineUtc\" > \"AssignedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_CourierAssignments_Couriers_CourierId",
                        column: x => x.CourierId,
                        principalTable: "Couriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CourierAssignments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CourierAssignments_CourierId",
                table: "CourierAssignments",
                column: "CourierId",
                unique: true,
                filter: "\"ReleasedAtUtc\" IS NULL AND \"DeliveredAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CourierAssignments_OrderId",
                table: "CourierAssignments",
                column: "OrderId",
                unique: true,
                filter: "\"ReleasedAtUtc\" IS NULL AND \"DeliveredAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CourierAssignments_PickedUpAtUtc_ReleasedAtUtc_PickupDeadli~",
                table: "CourierAssignments",
                columns: new[] { "PickedUpAtUtc", "ReleasedAtUtc", "PickupDeadlineUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CourierAssignments");
        }
    }
}
