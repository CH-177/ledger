namespace Ledger.Core.Models;

/// <summary>Entities whose timestamps are maintained automatically by <see cref="Data.LedgerDbContext"/>.</summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
