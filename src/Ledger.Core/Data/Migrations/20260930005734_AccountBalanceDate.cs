using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountBalanceDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BalanceColumn",
                table: "ImportProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BalanceDate",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            // Repair imports made before 2.4.2: an account's starting balance was the bank's balance on
            // the day the account was added, so imported transactions dated on or before that day were
            // counted twice. Those accounts get that day as their balance date, which makes those rows
            // history only. Accounts without such imports keep their old behaviour (no balance date).
            migrationBuilder.Sql("""
                UPDATE "Accounts" SET "BalanceDate" = date("CreatedAt", 'localtime')
                WHERE "BalanceDate" IS NULL AND EXISTS (
                    SELECT 1 FROM "Transactions" AS t
                    WHERE t."IsDeleted" = 0 AND t."ImportBatchId" IS NOT NULL
                      AND (t."AccountId" = "Accounts"."Id" OR t."TransferAccountId" = "Accounts"."Id")
                      AND t."Date" <= date("Accounts"."CreatedAt", 'localtime'));
                """);

            // Tidy payees 2.4.1 made from Example CU's QFX descriptions ("Withdrawal FRESHCO #078 Date
            // 09/24/2026" became "Freshco # Date"): drop the leftover " Date" and anything from a
            // store number on. Only imported rows; a payee typed by the user never looks like this.
            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "Payee" = rtrim(substr("Payee", 1, length("Payee") - 5), ' -#')
                WHERE "ImportBatchId" IS NOT NULL AND "Payee" LIKE '% Date' AND length("Payee") > 5;
                UPDATE "Transactions" SET "Payee" = rtrim(substr("Payee", 1, instr("Payee", ' #') - 1), ' -')
                WHERE "ImportBatchId" IS NOT NULL AND instr("Payee", ' #') > 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceColumn",
                table: "ImportProfiles");

            migrationBuilder.DropColumn(
                name: "BalanceDate",
                table: "Accounts");
        }
    }
}
