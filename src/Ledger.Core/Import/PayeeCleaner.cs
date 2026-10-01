using System.Globalization;
using System.Text.RegularExpressions;

namespace Ledger.Core.Import;

/// <summary>
/// Turns a bank's description into a readable payee: "POS PURCHASE QUICKMART 0412 RIVERTON PA"
/// becomes "Quickmart Riverton". It's deliberately conservative. The user corrects the result in
/// review once, and that correction is remembered for next time (import payee rules).
/// </summary>
public static partial class PayeeCleaner
{
    private static readonly string[] Prefixes =
    [
        "POS PURCHASE", "POS DEBIT", "POS WITHDRAWAL", "POS", "DEBIT CARD PURCHASE", "DEBIT PURCHASE", "CHECKCARD", "CHECK CARD",
        "VISA DDA PUR", "VISA PURCHASE", "PURCHASE AUTHORIZED ON", "PURCHASE", "RECURRING PAYMENT", "RECURRING DEBIT",
        "ACH DEBIT", "ACH CREDIT", "ACH WITHDRAWAL", "ACH DEPOSIT", "ACH", "ONLINE PAYMENT TO", "ONLINE TRANSFER TO",
        "ONLINE TRANSFER FROM", "ELECTRONIC WITHDRAWAL", "ELECTRONIC DEPOSIT", "EXTERNAL WITHDRAWAL", "EXTERNAL DEPOSIT",
        "WITHDRAWAL", "DEPOSIT", "PAYMENT TO", "DBT CRD", "SQ *", "TST*", "PAYPAL *", "PP*",
    ];

    public static string Clean(string description)
    {
        var s = Spaces().Replace(description.Trim().ToUpperInvariant(), " ");
        bool again;
        do
        {
            again = false;
            foreach (var p in Prefixes)
            {
                if (s.StartsWith(p + " ", StringComparison.Ordinal) || s.StartsWith(p, StringComparison.Ordinal) && p.EndsWith('*'))
                {
                    s = s[p.Length..].TrimStart(' ', '*', '-', ':');
                    again = true;
                }
            }
        } while (again && s.Length > 0);

        // Example CU's QFX files: "FRESHCO #078 DATE 09/24/2026". The purchase date isn't part of the name.
        s = PurchaseDate().Replace(s, " ");

        // Example CU's QFX transfers: "MOBILE FROM SHARE 0001" (your own share), "ONLINE BANKING FROM
        // PAT Q EXAMPLE 1234XXXXXX SHARE 0004" (someone else's; the payee is the person).
        if (BankTransfer().Match(s) is { Success: true } transfer)
        {
            var rest = transfer.Groups[2].Value;
            var who = Spaces().Replace(Share().Replace(Masked().Replace(rest, " "), " "), " ").Trim();
            var direction = transfer.Groups[1].Value == "FROM" ? "From" : "To";
            if (who.Length == 0)
            {
                return !Masked().IsMatch(rest) && Share().Match(rest) is { Success: true } share
                    ? $"Transfer {direction} Share {share.Groups[1].Value}"
                    : $"Transfer {direction} Another Member";
            }
            s = who;
        }

        // A store number marks the end of the name: "WM SUPERCENTER #1001 12 MAIN ST" is Wm Supercenter.
        if (HashNumber().Match(s) is { Success: true, Index: > 0 } hash)
        {
            s = s[..hash.Index];
        }

        s = RefundWord().Replace(s, " ");   // "TARGET RETURN" is Target: the refund is the kind, not the payee
        s = Parenthesized().Replace(s, " "); // "(S0001) (1234…)": account numbers, often cut off
        s = MonthYear().Replace(s, " ");    // "AUGUST 2026 DIVIDEND" is the same payee every month
        s = Percent().Replace(s, " ");      // "APY EARNED 2.700%"
        s = DatePart().Replace(s, " ");     // 09/26, 09/26/26
        s = CardMask().Replace(s, " ");     // XXXX1234, *1234, CARD 1234
        s = Reference().Replace(s, " ");    // "P123456AB1", "NO1234567": codes that change every time
        s = Digits().Replace(s, " ");       // store and reference numbers
        s = s.Replace('*', ' ').Replace('…', ' ');
        s = Hash().Replace(s, " ");         // "#0412"
        s = TrailingState().Replace(s, ""); // "... PA"
        s = Spaces().Replace(s, " ").Trim(' ', '-', '*', '#', ',', '.');
        if (s.Length == 0)
        {
            return description.Trim();
        }
        var all = s.Split(' ');
        // A lone letter left behind by a removed number ("TARGET T-1234") isn't part of the name.
        if (all.Length > 1 && all[^1].Length == 1) all = all[..^1];
        var words = all.Take(5);
        // Title case, keeping known acronyms (also inside "CVS/PHARMACY").
        return string.Join(' ', words.Select(w => string.Join('/', w.Split('/').Select(part => IsAcronym(part)
            ? part
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())))));
    }

    /// <summary>The key payee rules are remembered under: the cleaned description without case or digits.</summary>
    public static string RuleKey(string description) => Clean(description).ToUpperInvariant();

    private static bool IsAcronym(string w) => w is "ATM" or "USA" or "LLC" or "INC" or "CVS" or "BP" or "KFC" or "IHOP" or "UPS" or "USPS" or "AMC" or "APY";

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}(/\d{2,4})?\b")] private static partial Regex DatePart();
    [GeneratedRegex(@"(X{2,}\d{2,4}|\*+\d{2,4}|\bCARD\s*\d{2,4})")] private static partial Regex CardMask();
    [GeneratedRegex(@"\b\d{3,}\b")] private static partial Regex Digits();
    [GeneratedRegex(@"\b(?=(?:[A-Z]*\d){3})(?=\d*[A-Z])[A-Z0-9]{5,}\b")] private static partial Regex Reference();
    [GeneratedRegex(@"\([^)]*(\)|$)")] private static partial Regex Parenthesized();
    [GeneratedRegex(@"\b(JANUARY|FEBRUARY|MARCH|APRIL|MAY|JUNE|JULY|AUGUST|SEPTEMBER|OCTOBER|NOVEMBER|DECEMBER)\s+\d{4}\b")] private static partial Regex MonthYear();
    [GeneratedRegex(@"\d+(\.\d+)?%")] private static partial Regex Percent();
    [GeneratedRegex(@"\bDATE\s+\d{1,2}/\d{1,2}(/\d{2,4})?\b")] private static partial Regex PurchaseDate();
    [GeneratedRegex(@"^(?:MOBILE|ONLINE BANKING)\s+(FROM|TO)\s+(.*)$")] private static partial Regex BankTransfer();
    [GeneratedRegex(@"\bSHARE\s+(\d{4})\b")] private static partial Regex Share();
    [GeneratedRegex(@"\b\d+X{3,}\b")] private static partial Regex Masked();
    [GeneratedRegex(@"#\s*\d")] private static partial Regex HashNumber();
    [GeneratedRegex(@"\b(REFUND|RETURN|RETURNED|MERCHANDISE CREDIT)\b")] private static partial Regex RefundWord();
    [GeneratedRegex(@"#\s*\w*\d\w*")] private static partial Regex Hash();
    [GeneratedRegex(@"\s(AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC)$")]
    private static partial Regex TrailingState();
}
