namespace Ledger.Core.Import;

/// <summary>
/// One transaction as a bank file describes it, before any matching or categorising. Every
/// parser produces these, so the rest of the import never cares which format the file was.
/// AmountCents is signed: positive is money into the account, negative is money out.
/// Description is the bank's own text (e.g. "POS PURCHASE QUICKMART 0412 RIVERTON PA").
/// ExternalId is the bank's transaction ID when the file has one (QFX FITID, a CSV reference column).
/// BalanceCents is the bank's running balance after this row, when the file has a balance column.
/// </summary>
public sealed record ImportedRow(DateOnly Date, long AmountCents, string Description, string? Memo = null, string? ExternalId = null, int LineNumber = 0,
    long? BalanceCents = null);

/// <summary>The bank's own balance for the account at the end of <paramref name="Date"/>.</summary>
public sealed record BankBalance(DateOnly Date, long BalanceCents);

/// <summary>What a parser read, plus the lines it couldn't use (listed, never silently dropped).</summary>
public sealed record ParsedFile(IReadOnlyList<ImportedRow> Rows, IReadOnlyList<string> Problems, string? BankAccountKey = null, BankBalance? StatementBalance = null)
{
    /// <summary>
    /// The bank's balance at the end of the file's latest day: the statement balance for QFX, or for
    /// a CSV with a balance column, the running balance after that day's last transaction.
    /// </summary>
    public BankBalance? EndingBalance => StatementBalance ?? LastRunningBalance(Rows);

    private static BankBalance? LastRunningBalance(IReadOnlyList<ImportedRow> rows)
    {
        var withBalance = rows.Where(r => r.BalanceCents is not null).ToList();
        if (withBalance.Count == 0) return null;
        var lastDay = withBalance.Max(r => r.Date);
        var day = withBalance.Where(r => r.Date == lastDay).ToList();
        // The day's final row is the one no other row of that day follows: a row R follows F when
        // R's balance minus R's amount is F's balance. Files list days newest-first or oldest-first.
        var final = day.Where(f => !day.Any(r => r != f && r.BalanceCents - r.AmountCents == f.BalanceCents)).ToList();
        var pick = final.Count == 1
            ? final[0]
            : rows[0].Date >= rows[^1].Date ? day[0] : day[^1];
        return new BankBalance(lastDay, pick.BalanceCents!.Value);
    }
}
