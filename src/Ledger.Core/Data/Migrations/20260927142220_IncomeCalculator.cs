using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncomeCalculator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PayFrequency = table.Column<int>(type: "INTEGER", nullable: false),
                    PayPeriodAnchor = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PaydayOffsetDays = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPaydayConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorkweekStart = table.Column<int>(type: "INTEGER", nullable: false),
                    IsOvertimeEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    DepositAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    IncomeCategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    FilingStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    W4MultipleJobs = table.Column<bool>(type: "INTEGER", nullable: false),
                    W4DependentsCreditCents = table.Column<long>(type: "INTEGER", nullable: false),
                    W4OtherIncomeCents = table.Column<long>(type: "INTEGER", nullable: false),
                    W4DeductionsCents = table.Column<long>(type: "INTEGER", nullable: false),
                    W4ExtraWithholdingCents = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkLocationName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WorkPsdCode = table.Column<string>(type: "TEXT", maxLength: 6, nullable: true),
                    WorkNonResidentEitBasisPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkLstAnnualCents = table.Column<long>(type: "INTEGER", nullable: false),
                    IsLstExempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.CheckConstraint("CK_Job_Lst", "\"WorkLstAnnualCents\" BETWEEN 0 AND 5200");
                    table.CheckConstraint("CK_Job_PaydayOffset", "\"PaydayOffsetDays\" BETWEEN 0 AND 31");
                    table.CheckConstraint("CK_Job_WorkEit", "\"WorkNonResidentEitBasisPoints\" BETWEEN 0 AND 1000");
                    table.ForeignKey(
                        name: "FK_Jobs_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_Categories_IncomeCategoryId",
                        column: x => x.IncomeCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    HourlyRateCents = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRates", x => x.Id);
                    table.CheckConstraint("CK_JobRate_Positive", "\"HourlyRateCents\" > 0");
                    table.ForeignKey(
                        name: "FK_JobRates_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaycheckRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EstimatedNetCents = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaycheckRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaycheckRecords_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaycheckRecords_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PaidMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    UnpaidBreakMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                    table.CheckConstraint("CK_Shift_Break", "\"UnpaidBreakMinutes\" >= 0");
                    table.CheckConstraint("CK_Shift_Minutes", "\"PaidMinutes\" BETWEEN 1 AND 1440");
                    table.ForeignKey(
                        name: "FK_Shifts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRates_JobId_EffectiveDate",
                table: "JobRates",
                columns: new[] { "JobId", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DepositAccountId",
                table: "Jobs",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_IncomeCategoryId",
                table: "Jobs",
                column: "IncomeCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PaycheckRecords_JobId_PeriodStart",
                table: "PaycheckRecords",
                columns: new[] { "JobId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaycheckRecords_TransactionId",
                table: "PaycheckRecords",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_JobId_Date",
                table: "Shifts",
                columns: new[] { "JobId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRates");

            migrationBuilder.DropTable(
                name: "PaycheckRecords");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
