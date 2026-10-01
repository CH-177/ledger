using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportOutsideTransferRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOutsideTransfer",
                table: "ImportPayeeRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOutsideTransfer",
                table: "ImportPayeeRules");
        }
    }
}
