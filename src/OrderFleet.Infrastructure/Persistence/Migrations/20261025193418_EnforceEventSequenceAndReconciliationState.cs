using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEventSequenceAndReconciliationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "Payments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_AggregateId_AggregateSequence",
                table: "OutboxMessages",
                columns: new[] { "AggregateId", "AggregateSequence" },
                unique: true,
                filter: "\"AggregateSequence\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_AggregateId_AggregateSequence",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "Payments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
