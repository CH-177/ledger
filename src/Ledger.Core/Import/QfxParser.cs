using System.Globalization;
using System.Text.RegularExpressions;

namespace Ledger.Core.Import;

/// <summary>
/// Reads QFX/OFX statement downloads (Quicken's format), both the older SGML style (tags without
/// closing tags) and OFX 2 XML. Only what an import needs is read: each STMTTRN's posted date,
/// amount, bank ID (FITID), name and memo, plus the account ID so the file can be matched to an
/// account next time.
/// </summary>
public static partial class QfxParser
{
    public static bool LooksLikeOfx(string text) =>
        text.Contains("<OFX>", StringComparison.OrdinalIgnoreCase) || text.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase);

    public static ParsedFile Parse(string text)
    {
        var rows = new List<ImportedRow>();
        var problems = new List<string>();
        var account = Tag(text, "ACCTID");

        var blocks = TransactionBlock().Matches(text);
        if (blocks.Count == 0)
        {
            problems.Add("No transactions were found in this file.");
        }
        var n = 0;
        foreach (Match block in blocks)
        {
            n++;
            var body = block.Groups[1].Value;
            var date = ParseDate(Tag(body, "DTPOSTED") ?? Tag(body, "DTUSER"));
            var amount = Tag(body, "TRNAMT");
            if (date is null || amount is null || !decimal.TryParse(amount.Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                problems.Add($"Transaction {n}: missing or unreadable date or amount, so it was skipped.");
                continue;
            }
            var name = Tag(body, "NAME") ?? Tag(body, "PAYEE") ?? "";
            var memo = Tag(body, "MEMO");
            if (name.Length == 0 && memo is not null)
            {
                (name, memo) = (memo, null);
            }
            if (value == 0)
            {
                // e.g. Example CU's "Prenotification ACH Deposit" before a first direct deposit.
                problems.Add($"Transaction {n}: a $0 notice from the bank (\"{Unescape(name)}\"), not a transaction, so it was skipped.");
                continue;
            }
            rows.Add(new ImportedRow(date.Value, Money.RoundToCents(value * 100m), Unescape(name), memo is null ? null : Unescape(memo), Tag(body, "FITID"), n));
        }
        // The statement balance is "as of" the download moment, but it only contains posted
        // transactions, and those are all in the file. So it's the balance at the end of the last day
        // with a posted transaction: anything entered by hand after that isn't in the bank's number.
        var balance = LedgerBalance(text);
        if (balance is not null && rows.Count > 0 && rows.Max(r => r.Date) < balance.Date)
        {
            balance = balance with { Date = rows.Max(r => r.Date) };
        }
        return new ParsedFile(rows, problems, account, balance);
    }

    /// <summary>The statement's own closing balance: LEDGERBAL's BALAMT as of DTASOF.</summary>
    private static BankBalance? LedgerBalance(string text)
    {
        var m = Regex.Match(text, @"<LEDGERBAL>(.*?)(?:</LEDGERBAL>|(?=<AVAILBAL>)|(?=</STMTRS>))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var amount = Tag(m.Groups[1].Value, "BALAMT");
        var date = ParseDate(Tag(m.Groups[1].Value, "DTASOF"));
        return amount is not null && date is { } d && decimal.TryParse(amount.Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? new BankBalance(d, Money.RoundToCents(value * 100m))
            : null;
    }

    /// <summary>A tag's text: "&lt;NAME&gt;QUICKMART" (SGML, value runs to the next tag or line end) or "&lt;NAME&gt;QUICKMART&lt;/NAME&gt;".</summary>
    private static string? Tag(string text, string tag)
    {
        var m = Regex.Match(text, $@"<{tag}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Trim() is { Length: > 0 } v ? v : null;
    }

    /// <summary>OFX dates: YYYYMMDD, optionally followed by a time and "[-5:EST]". Only the date matters here.</summary>
    internal static DateOnly? ParseDate(string? value) =>
        value is { Length: >= 8 } && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Unescape(string s) => s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Trim();

    [GeneratedRegex(@"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TransactionBlock();
}
