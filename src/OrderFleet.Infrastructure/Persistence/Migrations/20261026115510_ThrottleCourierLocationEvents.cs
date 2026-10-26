using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThrottleCourierLocationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastEventAssignmentId",
                table: "CourierPositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastEventAtUtc",
                table: "CourierPositions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Point>(
                name: "LastEventLocation",
                table: "CourierPositions",
                type: "geography(Point,4326)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CourierPosition_Emission",
                table: "CourierPositions",
                sql: "(\"LastEventLocation\" IS NULL AND \"LastEventAtUtc\" IS NULL AND \"LastEventAssignmentId\" IS NULL) OR (\"LastEventLocation\" IS NOT NULL AND \"LastEventAtUtc\" IS NOT NULL AND \"LastEventAssignmentId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CourierPosition_Emission",
                table: "CourierPositions");

            migrationBuilder.DropColumn(
                name: "LastEventAssignmentId",
                table: "CourierPositions");

            migrationBuilder.DropColumn(
                name: "LastEventAtUtc",
                table: "CourierPositions");

            migrationBuilder.DropColumn(
                name: "LastEventLocation",
                table: "CourierPositions");
        }
    }
}
