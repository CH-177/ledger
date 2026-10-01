namespace Ledger.Core.Models;

/// <summary>
/// One import of a bank file. Every transaction it created carries its id, so "Undo this import"
/// removes exactly those transactions and nothing else.
/// </summary>
public class ImportBatch
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public required string FileName { get; set; }
    public DateTime ImportedAt { get; set; }
    public int TransactionCount { get; set; }
    public bool IsUndone { get; set; }
}

/// <summary>
/// How to read one account's bank file, remembered after the first import. For CSV, which header
/// holds each field; for QFX, just the bank's account id so the right Ledger account is picked.
/// </summary>
public class ImportProfile
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>The account number or id inside the bank's file (QFX ACCTID), for matching the account automatically.</summary>
    public string? BankAccountKey { get; set; }

    public string? DateColumn { get; set; }
    public string? DescriptionColumn { get; set; }
    public string? AmountColumn { get; set; }
    public string? DebitColumn { get; set; }
    public string? CreditColumn { get; set; }
    public string? MemoColumn { get; set; }
    public string? ReferenceColumn { get; set; }
    public string? BalanceColumn { get; set; }
    public string? DateFormat { get; set; }
    public bool InvertSign { get; set; }
}

/// <summary>
/// "Descriptions like this are this payee, in this category": learned when the user corrects a row
/// in review and ticks "Remember". Applied to future imports as a suggestion they still see.
/// </summary>
public class ImportPayeeRule
{
    public int Id { get; set; }

    /// <summary>The cleaned bank description in upper case (see PayeeCleaner.RuleKey).</summary>
    public required string Key { get; set; }

    public required string Payee { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>For a transfer between the user's own accounts: the other account (e.g. "Transfer from Main Savings" → Savings).</summary>
    /// <remarks>Deliberately not a foreign key (adding one would rebuild the table); an id that no longer matches an open account is ignored.</remarks>
    public int? TransferAccountId { get; set; }

    /// <summary>A transfer with someone else's account (e.g. a family member's): neither income nor spending.</summary>
    public bool IsOutsideTransfer { get; set; }
}
