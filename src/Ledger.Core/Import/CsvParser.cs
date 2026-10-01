using System.Globalization;
using System.Text;

namespace Ledger.Core.Import;

/// <summary>
/// Which columns of a bank's CSV hold what. Columns are named by their header text so a layout
/// keeps working if the bank reorders columns. Either <see cref="Amount"/> (signed) or
/// <see cref="Debit"/>/<see cref="Credit"/> (two unsigned columns) is used.
/// </summary>
public sealed record CsvMapping(
    string Date,
    string Description,
    string? Amount = null,
    string? Debit = null,
    string? Credit = null,
    string? Memo = null,
    string? Reference = null,
    string? DateFormat = null,
    bool InvertSign = false,
    string? Balance = null)
{
    public bool UsesTwoColumns => Amount is null;
}

/// <summary>
/// Reads bank CSV exports: RFC 4180 quoting, a header row, and the mapping above. The mapping can
/// be guessed from common header names (<see cref="Detect"/>); the user confirms or corrects it once
/// per account and it's remembered.
/// </summary>
public static class CsvParser
{
    private static readonly string[] DateFormats =
        ["M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "yyyy-MM-dd", "yyyy/MM/dd", "M-d-yyyy", "dd-MMM-yyyy", "MMM d, yyyy", "d MMM yyyy"];

    /// <summary>Splits CSV text into records of fields, honouring quotes (including quoted commas and line breaks).</summary>
    public static IReadOnlyList<string[]> ReadRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': fields.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    fields.Add(field.ToString()); field.Clear();
                    if (fields.Any(f => f.Length > 0)) records.Add([.. fields]);
                    fields.Clear();
                    break;
                default: field.Append(c); break;
            }
        }
        fields.Add(field.ToString());
        if (fields.Any(f => f.Length > 0)) records.Add([.. fields]);
        return records;
    }

    /// <summary>The header row: the first record that has a date-like column name.</summary>
    public static (int Index, string[] Headers)? FindHeader(IReadOnlyList<string[]> records)
    {
        for (var i = 0; i < Math.Min(records.Count, 15); i++)
        {
            var cells = records[i].Select(h => h.Trim()).ToArray();
            if (cells.Any(h => Is(h, "date", "posted date", "posting date", "transaction date", "trans date", "effective date")))
            {
                return (i, cells);
            }
        }
        return null;
    }

    /// <summary>Guesses the mapping from header names. Returns null when no date or description column is recognisable.</summary>
    public static CsvMapping? Detect(string[] headers)
    {
        string? Find(params string[] names) => headers.FirstOrDefault(h => Is(h, names));
        string? Containing(string part) => headers.FirstOrDefault(h => h.Contains(part, StringComparison.OrdinalIgnoreCase));

        var date = Find("posted date", "posting date", "date", "transaction date", "trans date", "effective date");
        var description = Find("description", "payee", "name", "merchant", "transaction description", "details") ?? Containing("description");
        if (date is null || description is null)
        {
            return null;
        }
        var amount = Find("amount", "transaction amount", "amt");
        var debit = Find("debit", "withdrawal", "withdrawals", "amount debit", "debit amount", "money out") ?? Containing("debit");
        var credit = Find("credit", "deposit", "deposits", "amount credit", "credit amount", "money in") ?? Containing("credit");
        var memo = Find("memo", "notes", "extended description", "note");
        var reference = Find("reference", "reference number", "transaction id", "id", "check number", "ref");
        var balance = Find("balance", "running balance", "ledger balance", "account balance");

        return amount is not null
            ? new CsvMapping(date, description, Amount: amount, Memo: memo == description ? null : memo, Reference: reference, Balance: balance)
            : debit is not null || credit is not null
                ? new CsvMapping(date, description, Debit: debit, Credit: credit, Memo: memo == description ? null : memo, Reference: reference, Balance: balance)
                : null;
    }

    /// <summary>Whether every column the mapping names is among <paramref name="headers"/> (case-insensitive).</summary>
    public static bool Fits(CsvMapping mapping, IReadOnlyCollection<string> headers)
    {
        bool Has(string? name) => name is null || headers.Any(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
        return Has(mapping.Date) && Has(mapping.Description) && Has(mapping.Amount) && Has(mapping.Debit) && Has(mapping.Credit)
            && Has(mapping.Memo) && Has(mapping.Reference) && Has(mapping.Balance) && (mapping.Amount is not null || mapping.Debit is not null || mapping.Credit is not null);
    }

    /// <summary>Reads every data row with the mapping. Unreadable rows are listed in Problems, never silently dropped.</summary>
    public static ParsedFile Parse(string text, CsvMapping mapping)
    {
        var records = ReadRecords(text);
        var problems = new List<string>();
        if (FindHeader(records) is not { } header)
        {
            return new ParsedFile([], ["This file has no header row with a date column."]);
        }
        int Col(string? name) => name is null ? -1 : Array.FindIndex(header.Headers, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        int date = Col(mapping.Date), description = Col(mapping.Description), amount = Col(mapping.Amount),
            debit = Col(mapping.Debit), credit = Col(mapping.Credit), memo = Col(mapping.Memo), reference = Col(mapping.Reference),
            balance = Col(mapping.Balance);
        if (date < 0 || description < 0 || (amount < 0 && debit < 0 && credit < 0))
        {
            return new ParsedFile([], ["The columns chosen for date, description and amount aren't all in this file."]);
        }

        var rows = new List<ImportedRow>();
        for (var i = header.Index + 1; i < records.Count; i++)
        {
            var r = FitToHeader(records[i], header.Headers.Length, description);
            string Cell(int c) => c >= 0 && c < r.Length ? r[c].Trim() : "";
            var line = i + 1;

            if (ParseDate(Cell(date), mapping.DateFormat) is not { } d)
            {
                // Banks often end exports with a totals or disclaimer line; only mention real-looking rows.
                if (Cell(description).Length > 0 || Cell(amount).Length > 0) problems.Add($"Line {line}: the date \"{Cell(date)}\" isn't readable, so it was skipped.");
                continue;
            }

            long cents;
            if (amount >= 0)
            {
                if (ParseAmount(Cell(amount)) is not { } a) { problems.Add($"Line {line}: the amount \"{Cell(amount)}\" isn't readable, so it was skipped."); continue; }
                cents = a;
            }
            else
            {
                var outCents = ParseAmount(Cell(debit));
                var inCents = ParseAmount(Cell(credit));
                if (outCents is null && inCents is null) { problems.Add($"Line {line}: no amount, so it was skipped."); continue; }
                // Some banks write debits as negative numbers in the debit column; either way it's money out.
                cents = Math.Abs(inCents ?? 0) - Math.Abs(outCents ?? 0);
            }
            if (mapping.InvertSign) cents = -cents;
            if (cents == 0) { problems.Add($"Line {line}: a $0 notice from the bank (\"{Cell(description)}\"), not a transaction, so it was skipped."); continue; }

            var memoText = Cell(memo);
            var id = Cell(reference);
            // The bank's running balance, read like amounts (a card export may need the same sign flip).
            long? running = balance >= 0 && ParseAmount(Cell(balance)) is { } b ? (mapping.InvertSign ? -b : b) : null;
            rows.Add(new ImportedRow(d, cents, Cell(description), memoText.Length > 0 ? memoText : null, id.Length > 0 ? id : null, line, running));
        }
        return new ParsedFile(rows, problems);
    }

    /// <summary>
    /// Some banks (Example CU among them) don't quote a description that contains a comma, so
    /// "ACH Deposit From RIVERSIDE MARKET, INC." arrives as two fields and pushes every later column along.
    /// Extra fields are folded back into the description; empty trailing fields are just dropped.
    /// </summary>
    internal static string[] FitToHeader(string[] record, int headerLength, int descriptionColumn)
    {
        var r = record;
        while (r.Length > headerLength && r[^1].Trim().Length == 0) r = r[..^1];
        if (r.Length <= headerLength || descriptionColumn < 0) return r;
        var extra = r.Length - headerLength;
        return [.. r[..descriptionColumn], string.Join(",", r[descriptionColumn..(descriptionColumn + extra + 1)]), .. r[(descriptionColumn + extra + 1)..]];
    }

    /// <summary>"$1,234.56", "-12.30", "(12.30)" (accounting negative), "12.30-" and "12.30 CR" style values.</summary>
    internal static long? ParseAmount(string text)
    {
        var t = text.Trim().Replace("$", "").Replace(",", "").Replace(" ", "");
        if (t.Length == 0) return null;
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith("CR", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
        else if (t.EndsWith("DR", StringComparison.OrdinalIgnoreCase)) { negative = true; t = t[..^2]; }
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        var cents = Money.RoundToCents(value * 100m);
        return negative ? -Math.Abs(cents) : cents;
    }

    internal static DateOnly? ParseDate(string text, string? format)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        if (t.Length > 10 && t[10] == 'T') t = t[..10]; // ISO date-time
        else if (t.Contains(' ') && !char.IsLetter(t[0])) t = t[..t.IndexOf(' ')]; // "09/26/2026 00:00:00"
        string[] formats = format is null ? DateFormats : [format];
        return DateOnly.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static bool Is(string header, params string[] names) =>
        names.Any(n => string.Equals(header.Trim(), n, StringComparison.OrdinalIgnoreCase));
}
