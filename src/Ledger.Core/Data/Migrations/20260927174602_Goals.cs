using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Goals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Goal_CurrentNonNegative",
                table: "Goals");

            // Hand-corrected: the scaffold renamed CurrentAmountCents to Tracking, which would have turned a
            // saved amount into a tracking mode. The amount already saved becomes the starting amount, and
            // every existing goal is a manual one.
            migrationBuilder.RenameColumn(
                name: "CurrentAmountCents",
                table: "Goals",
                newName: "StartingAmountCents");

            migrationBuilder.AddColumn<int>(
                name: "Tracking",
                table: "Goals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GoalContributions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GoalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoalContributions", x => x.Id);
                    table.CheckConstraint("CK_GoalContribution_NonZero", "\"AmountCents\" <> 0");
                    table.ForeignKey(
                        name: "FK_GoalContributions_Goals_GoalId",
                        column: x => x.GoalId,
                        principalTable: "Goals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Goal_AccountTracking",
                table: "Goals",
                sql: "\"Tracking\" = 0 OR \"AccountId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Goal_StartingNonNegative",
                table: "Goals",
                sql: "\"StartingAmountCents\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_GoalContributions_GoalId",
                table: "GoalContributions",
                column: "GoalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoalContributions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Goal_AccountTracking",
                table: "Goals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Goal_StartingNonNegative",
                table: "Goals");

            migrationBuilder.DropColumn(
                name: "Tracking",
                table: "Goals");

            migrationBuilder.RenameColumn(
                name: "StartingAmountCents",
                table: "Goals",
                newName: "CurrentAmountCents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Goal_CurrentNonNegative",
                table: "Goals",
                sql: "\"CurrentAmountCents\" >= 0");
        }
    }
}
