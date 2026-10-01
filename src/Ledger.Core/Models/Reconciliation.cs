namespace Ledger.Core.Models;

/// <summary>
/// One statement matched against Ledger: the account's balance at the end of
/// <see cref="StatementDate"/> was <see cref="StatementBalanceCents"/>, and the transactions ticked
/// for it (their <see cref="Transaction.ReconciliationId"/>) are marked Reconciled.
/// </summary>
public class Reconciliation
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public DateOnly StatementDate { get; set; }

    /// <summary>Signed like account balances: a card statement's amount owed is negative.</summary>
    public long StatementBalanceCents { get; set; }

    public DateTime ReconciledAt { get; set; }
    public int TransactionCount { get; set; }
    public bool IsUndone { get; set; }
}
