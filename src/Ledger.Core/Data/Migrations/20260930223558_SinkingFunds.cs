using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SinkingFunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaidFromGoalId",
                table: "Transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSinkingFund",
                table: "Goals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MonthlySetAsideCents",
                table: "Goals",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaidFromGoalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "IsSinkingFund",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "MonthlySetAsideCents",
                table: "Goals");
        }
    }
}
