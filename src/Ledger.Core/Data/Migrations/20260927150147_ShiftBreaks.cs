using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShiftBreaks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN DATA MIGRATION. The scaffolded version dropped PaidMinutes and renamed
            // UnpaidBreakMinutes to ScheduledMinutes, which would have destroyed every shift's hours.
            // Order matters: add the new columns, copy the data across, and only then drop the old ones.
            migrationBuilder.AddColumn<int>(
                name: "ScheduledMinutes",
                table: "Shifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BreakOverrideMinutes",
                table: "Shifts",
                type: "INTEGER",
                nullable: true);

            // Shift length = what was paid + the break that was taken off. A break the user or the
            // schedule stated explicitly is kept as an override, so those shifts pay exactly as
            // before; shifts without one now follow the job's break rule.
            migrationBuilder.Sql(
                "UPDATE \"Shifts\" SET " +
                "\"ScheduledMinutes\" = \"PaidMinutes\" + \"UnpaidBreakMinutes\", " +
                "\"BreakOverrideMinutes\" = CASE WHEN \"UnpaidBreakMinutes\" > 0 THEN \"UnpaidBreakMinutes\" ELSE NULL END;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shift_Break",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shift_Minutes",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "PaidMinutes",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "UnpaidBreakMinutes",
                table: "Shifts");

            migrationBuilder.AddColumn<int>(
                name: "PaidBreakMinutes",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "UnpaidBreakAfterMinutes",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "UnpaidBreakMinutes",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shift_Break",
                table: "Shifts",
                sql: "\"BreakOverrideMinutes\" IS NULL OR \"BreakOverrideMinutes\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shift_Minutes",
                table: "Shifts",
                sql: "\"ScheduledMinutes\" BETWEEN 1 AND 1440");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Job_Breaks",
                table: "Jobs",
                sql: "\"UnpaidBreakMinutes\" BETWEEN 0 AND 240 AND \"UnpaidBreakAfterMinutes\" BETWEEN 0 AND 1440 AND \"PaidBreakMinutes\" BETWEEN 0 AND 240");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Shift_Break",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shift_Minutes",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Job_Breaks",
                table: "Jobs");

            // Reverse: restore paid minutes using each shift's override or the job's rule.
            migrationBuilder.AddColumn<int>(
                name: "PaidMinutes",
                table: "Shifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnpaidBreakMinutes",
                table: "Shifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE \"Shifts\" SET \"UnpaidBreakMinutes\" = COALESCE(\"BreakOverrideMinutes\", " +
                "(SELECT CASE WHEN \"Shifts\".\"ScheduledMinutes\" >= j.\"UnpaidBreakAfterMinutes\" THEN j.\"UnpaidBreakMinutes\" ELSE 0 END " +
                "FROM \"Jobs\" j WHERE j.\"Id\" = \"Shifts\".\"JobId\"), 0);");
            migrationBuilder.Sql("UPDATE \"Shifts\" SET \"PaidMinutes\" = \"ScheduledMinutes\" - \"UnpaidBreakMinutes\";");

            migrationBuilder.DropColumn(
                name: "BreakOverrideMinutes",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "ScheduledMinutes",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "PaidBreakMinutes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "UnpaidBreakAfterMinutes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "UnpaidBreakMinutes",
                table: "Jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shift_Break",
                table: "Shifts",
                sql: "\"UnpaidBreakMinutes\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shift_Minutes",
                table: "Shifts",
                sql: "\"PaidMinutes\" BETWEEN 1 AND 1440");
        }
    }
}
