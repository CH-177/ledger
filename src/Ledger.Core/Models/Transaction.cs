namespace Ledger.Core.Models;

/// <summary>
/// An actual, historical movement of money. Forecasts and plans are never stored here.
/// </summary>
/// <remarks>
/// A transfer is a single row moving <see cref="AmountCents"/> from <see cref="AccountId"/>
/// to <see cref="TransferAccountId"/>, so its two sides can never fall out of sync.
/// </remarks>
public class Transaction : IAuditable
{
    public int Id { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>Destination account. Required for transfers, null otherwise.</summary>
    public int? TransferAccountId { get; set; }
    public Account? TransferAccount { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Positive magnitude in cents. Direction comes from <see cref="TransactionType"/>.</summary>
    public long AmountCents { get; set; }

    public TransactionType TransactionType { get; set; }
    public string? Payee { get; set; }
    public string? Memo { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Cleared;

    /// <summary>
    /// The statement this was reconciled with (see <see cref="Reconciliation"/>), so undoing that
    /// reconciliation unmarks exactly these. Deliberately not a foreign key (adding one would rebuild the table).
    /// </summary>
    public int? ReconciliationId { get; set; }

    /// <summary>
    /// Spending paid from a sinking fund (a <see cref="Goal"/>): it lowers what's set aside there. It
    /// still counts as spending in its category. Deliberately not a foreign key (adding one would rebuild the table).
    /// </summary>
    public int? PaidFromGoalId { get; set; }

    /// <summary>The recurring rule this transaction realised, if any.</summary>
    public int? RecurringTransactionId { get; set; }
    public RecurringTransaction? RecurringTransaction { get; set; }

    /// <summary>
    /// The scheduled occurrence date this transaction realised. Together with
    /// <see cref="RecurringTransactionId"/> it is unique, which makes posting
    /// recurring transactions idempotent.
    /// </summary>
    public DateOnly? RecurrenceDate { get; set; }

    /// <summary>True when Ledger recorded this from a recurring rule set to record automatically.</summary>
    public bool IsAutoRecorded { get; set; }

    /// <summary>The bank-file import that created this transaction, if any.</summary>
    public int? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }

    /// <summary>Identifier supplied by an import source, used for duplicate detection.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Soft deletion: deleted transactions are excluded from every calculation but kept on disk.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
