using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaycheckDeductionsAndPaystubs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobDeductions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PercentBasisPoints = table.Column<int>(type: "INTEGER", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobDeductions", x => x.Id);
                    table.CheckConstraint("CK_JobDeduction_Amount", "\"AmountCents\" >= 0");
                    table.CheckConstraint("CK_JobDeduction_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
                    table.CheckConstraint("CK_JobDeduction_Percent", "\"PercentBasisPoints\" IS NULL OR \"PercentBasisPoints\" BETWEEN 1 AND 10000");
                    table.ForeignKey(
                        name: "FK_JobDeductions_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Paystubs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StubPeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    StubPeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PayDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PaidMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    GrossCents = table.Column<long>(type: "INTEGER", nullable: false),
                    FederalCents = table.Column<long>(type: "INTEGER", nullable: true),
                    SocialSecurityCents = table.Column<long>(type: "INTEGER", nullable: true),
                    MedicareCents = table.Column<long>(type: "INTEGER", nullable: true),
                    StateCents = table.Column<long>(type: "INTEGER", nullable: true),
                    StateUnemploymentCents = table.Column<long>(type: "INTEGER", nullable: true),
                    LocalEitCents = table.Column<long>(type: "INTEGER", nullable: true),
                    LocalServicesTaxCents = table.Column<long>(type: "INTEGER", nullable: true),
                    NetCents = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Paystubs", x => x.Id);
                    table.CheckConstraint("CK_Paystub_Amounts", "\"GrossCents\" >= 0 AND \"NetCents\" >= 0");
                    table.ForeignKey(
                        name: "FK_Paystubs_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobDeductions_JobId",
                table: "JobDeductions",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Paystubs_JobId_PeriodStart",
                table: "Paystubs",
                columns: new[] { "JobId", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobDeductions");

            migrationBuilder.DropTable(
                name: "Paystubs");
        }
    }
}
