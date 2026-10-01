using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledger.Core.Data;

/// <summary>Used only by <c>dotnet ef</c> to build migrations. Never used at runtime.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new LedgerDbContext(options);
    }
}
