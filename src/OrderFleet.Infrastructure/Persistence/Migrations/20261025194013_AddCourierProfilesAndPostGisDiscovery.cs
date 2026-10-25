using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCourierProfilesAndPostGisDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "Couriers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    LocationObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Couriers", x => x.Id);
                    table.CheckConstraint("CK_Courier_Assignment", "(\"Status\" IN ('Offline','Available') AND \"OrderId\" IS NULL AND \"AssignmentId\" IS NULL) OR (\"Status\" IN ('Reserved','Busy') AND \"OrderId\" IS NOT NULL AND \"AssignmentId\" IS NOT NULL)");
                    table.CheckConstraint("CK_Courier_Location", "(\"Latitude\" IS NULL AND \"Longitude\" IS NULL AND \"LocationObservedAt\" IS NULL) OR (\"Latitude\" IS NOT NULL AND \"Longitude\" IS NOT NULL AND \"LocationObservedAt\" IS NOT NULL AND \"Latitude\" BETWEEN -90 AND 90 AND \"Longitude\" BETWEEN -180 AND 180)");
                    table.ForeignKey(
                        name: "FK_Couriers_AspNetUsers_Id",
                        column: x => x.Id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourierPositions",
                columns: table => new
                {
                    CourierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Location = table.Column<Point>(type: "geography(Point,4326)", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourierPositions", x => x.CourierId);
                    table.ForeignKey(
                        name: "FK_CourierPositions_Couriers_CourierId",
                        column: x => x.CourierId,
                        principalTable: "Couriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CourierPositions_Location",
                table: "CourierPositions",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_CourierPositions_ObservedAt",
                table: "CourierPositions",
                column: "ObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Couriers_AssignmentId",
                table: "Couriers",
                column: "AssignmentId",
                unique: true,
                filter: "\"AssignmentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Couriers_OrderId",
                table: "Couriers",
                column: "OrderId",
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CourierPositions");

            migrationBuilder.DropTable(
                name: "Couriers");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
