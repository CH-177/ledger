using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Core.Data.Migrations
{
    /// <summary>
    /// 3.0.0: a fresh install no longer assumes a home location for local tax. This migration once
    /// wrote the old built-in default into pre-3.0 data that relied on it; the only such data has
    /// been upgraded, so it now changes nothing. It stays so the migration history is unchanged.
    /// </summary>
    public partial class HomeTaxDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
