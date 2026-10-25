using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveDecimalScaleAndCheckMoney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TotalAmount",
                table: "Orders",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(31,2)",
                oldPrecision: 31,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "OrderItems",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(31,2)",
                oldPrecision: 31,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPrice",
                table: "OrderItems",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(31,2)",
                oldPrecision: 31,
                oldScale: 2);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_TotalAmount",
                table: "Orders",
                sql: "\"TotalAmount\" > 0 AND \"TotalAmount\" <= 79228162514264337593543950335 AND \"TotalAmount\" = trunc(\"TotalAmount\", 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_TotalPrice",
                table: "OrderItems",
                sql: "\"TotalPrice\" > 0 AND \"TotalPrice\" <= 79228162514264337593543950335 AND \"TotalPrice\" = trunc(\"TotalPrice\", 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_UnitPrice",
                table: "OrderItems",
                sql: "\"UnitPrice\" > 0 AND \"UnitPrice\" <= 79228162514264337593543950335 AND \"UnitPrice\" = trunc(\"UnitPrice\", 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_TotalAmount",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_TotalPrice",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_UnitPrice",
                table: "OrderItems");

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalAmount",
                table: "Orders",
                type: "numeric(31,2)",
                precision: 31,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "OrderItems",
                type: "numeric(31,2)",
                precision: 31,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPrice",
                table: "OrderItems",
                type: "numeric(31,2)",
                precision: 31,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");
        }
    }
}
