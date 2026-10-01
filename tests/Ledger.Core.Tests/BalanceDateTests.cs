using Ledger.Core.Import;
using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class BalanceDateTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    [Fact]
    public async Task Transactions_on_or_before_the_balance_date_are_already_in_the_starting_balance()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var accounts = new AccountService(db.Factory, db.Time);
        var tx = new TransactionService(db.Factory, db.Time);
        var checking = await accounts.CreateAsync(new AccountInput("Checking", AccountType.Checking, 10000, BalanceDate: D(9, 26)));

        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 700, D(9, 20), checking.Id)); // already in the $100
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 300, D(9, 26), checking.Id)); // the day itself: already in it
        await tx.CreateAsync(new TransactionInput(TransactionType.Income, 1500, D(9, 27), checking.Id));

        Assert.Equal(10000 + 1500, await accounts.GetBalanceAsync(checking.Id));
        Assert.Equal(10000, await accounts.GetBalanceAsync(checking.Id, D(9, 26)));
        Assert.Equal(10000 + 300, await accounts.GetBalanceAsync(checking.Id, D(9, 25)));       // history before it still adds up
        Assert.Equal(10000 + 300 + 700, await accounts.GetBalanceAsync(checking.Id, D(9, 19)));
        // It's still real spending: totals don't care about the balance date.
        Assert.Equal(1000, (await tx.GetTotalsAsync(new TransactionQuery())).ExpenseCents);
    }

    [Fact]
    public async Task Using_the_banks_balance_makes_that_day_match_whatever_is_imported_later()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var accounts = new AccountService(db.Factory, db.Time);
        var import = new ImportService(db.Factory, db.Time);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 1047); // entered from the bank, no date

        await accounts.SetBalanceFromBankAsync(checking.Id, D(9, 27), 3150);
        var file = new ParsedFile([new ImportedRow(D(9, 25), -200, "FRESHCO", ExternalId: "a"), new ImportedRow(D(9, 27), 1500, "TRANSFER FROM PAT", ExternalId: "b"),
            new ImportedRow(D(9, 29), -299, "APPLE.COM/BILL", ExternalId: "c")], []);
        var preview = await import.PreviewAsync(checking.Id, file);
        await import.CommitAsync(checking.Id, file, preview.Rows.Select(r => new ImportDecision(r.Index, r.Type, r.Payee, r.CategoryId)).ToList(), "a.qfx");

        Assert.Equal(3150, await accounts.GetBalanceAsync(checking.Id, D(9, 27)));
        Assert.Equal(3150 - 299, await accounts.GetBalanceAsync(checking.Id));
    }

    [Fact]
    public void A_qfx_statement_balance_belongs_to_its_last_posted_day_and_zero_rows_are_skipped()
    {
        const string qfx = """
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>123S0002</BANKACCTFROM><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260925120000.000[-4:EDT]<TRNAMT>-2.00<FITID>1<NAME>FRESHCO #078
            <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260923120000.000[-4:EDT]<TRNAMT>0.00<FITID>2<NAME>Prenotification ACH Deposit
            <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260927120000.000[-4:EDT]<TRNAMT>15.00<FITID>3<NAME>Transfer from Pat
            </BANKTRANLIST><LEDGERBAL><BALAMT>31.50<DTASOF>20260929201901.975[-4:EDT]</LEDGERBAL><AVAILBAL><BALAMT>18.04<DTASOF>20260929201901.975[-4:EDT]</AVAILBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var file = QfxParser.Parse(qfx);
        Assert.Equal(2, file.Rows.Count);
        Assert.Contains("$0 notice", Assert.Single(file.Problems));
        Assert.Equal(new BankBalance(D(9, 27), 3150), file.EndingBalance); // the ledger balance, not "available"
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_csv_running_balance_gives_the_balance_at_the_end_of_the_last_day(bool newestFirst)
    {
        string[] lines =
        [
            "9/24/2026,AMC,-7.41,17.75",
            "9/25/2026,WM SUPERCENTER,-5.28,12.47",
            "9/25/2026,FRESHCO,-2.00,10.47",
        ];
        if (newestFirst) Array.Reverse(lines);
        var csv = "Date,Transaction Description,Amount,Balance\n" + string.Join("\n", lines) + "\n";
        var mapping = CsvParser.Detect(CsvParser.FindHeader(CsvParser.ReadRecords(csv))!.Value.Headers)!;
        Assert.Equal("Balance", mapping.Balance);
        Assert.Equal(new BankBalance(D(9, 25), 1047), CsvParser.Parse(csv, mapping).EndingBalance);
    }
}
