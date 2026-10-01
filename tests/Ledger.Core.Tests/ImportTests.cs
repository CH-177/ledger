using Ledger.Core.Import;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class ImportParserTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private const string SgmlQfx = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX>
        <BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>231381116<ACCTID>0012345678<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20260901<DTEND>20260930
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260912120000[-5:EST]<TRNAMT>-8.42<FITID>2026091201<NAME>POS PURCHASE QUICKMART 0412<MEMO>RIVERTON PA
        <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260925<TRNAMT>163.12<FITID>2026092502<NAME>RIVERSIDE PAYROLL
        </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
        """;

    [Fact]
    public void Reads_sgml_qfx_with_bank_ids_and_account_number()
    {
        Assert.True(QfxParser.LooksLikeOfx(SgmlQfx));
        var file = QfxParser.Parse(SgmlQfx);
        Assert.Empty(file.Problems);
        Assert.Equal("0012345678", file.BankAccountKey);
        Assert.Equal(
            [(D(9, 12), -842L, "POS PURCHASE QUICKMART 0412", "RIVERTON PA", "2026091201"), (D(9, 25), 16312L, "RIVERSIDE PAYROLL", (string?)null, "2026092502")],
            file.Rows.Select(r => (r.Date, r.AmountCents, r.Description, r.Memo, r.ExternalId)));
    }

    [Fact]
    public void Reads_xml_ofx_too()
    {
        const string xml = """
            <?xml version="1.0"?><?OFX OFXHEADER="200" VERSION="220"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>999</ACCTID></BANKACCTFROM><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261001</DTPOSTED><TRNAMT>-11.99</TRNAMT><FITID>A1</FITID><NAME>SPOTIFY &amp; CO</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var row = Assert.Single(QfxParser.Parse(xml).Rows);
        Assert.Equal((D(10, 1), -1199L, "SPOTIFY & CO", "A1"), (row.Date, row.AmountCents, row.Description, row.ExternalId));
    }

    [Fact]
    public void Csv_quotes_commas_and_line_breaks_are_honoured()
    {
        var records = CsvParser.ReadRecords("a,\"b, c\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",2,3\n");
        Assert.Equal(["a", "b, c", "say \"hi\""], records[0]);
        Assert.Equal(["multi\nline", "2", "3"], records[1]);
    }

    [Theory]
    [InlineData("$1,234.56", 123456L)]
    [InlineData("-12.30", -1230L)]
    [InlineData("(12.30)", -1230L)]
    [InlineData("12.30-", -1230L)]
    [InlineData("12.30 CR", 1230L)]
    [InlineData("12.30 DR", -1230L)]
    [InlineData("", null)]
    [InlineData("n/a", null)]
    public void Csv_amounts_in_every_bank_style(string text, long? cents) => Assert.Equal(cents, CsvParser.ParseAmount(text));

    [Fact]
    public void A_signed_amount_csv_is_detected_and_read()
    {
        const string csv = "Account Name: Checking\nPosted Date,Description,Amount,Memo,Reference\n09/12/2026,QUICKMART 0412,-8.42,,R1\n9/25/2026,\"RIVERSIDE MARKET, INC PAYROLL\",163.12,Direct dep,R2\nTotal,,154.70,,\n";
        var header = CsvParser.FindHeader(CsvParser.ReadRecords(csv));
        var mapping = CsvParser.Detect(header!.Value.Headers);
        Assert.Equal(new CsvMapping("Posted Date", "Description", Amount: "Amount", Memo: "Memo", Reference: "Reference"), mapping);

        var file = CsvParser.Parse(csv, mapping!);
        Assert.Equal([(D(9, 12), -842L, "R1"), (D(9, 25), 16312L, "R2")], file.Rows.Select(r => (r.Date, r.AmountCents, r.ExternalId)));
        Assert.Equal("RIVERSIDE MARKET, INC PAYROLL", file.Rows[1].Description);
        // The bank's "Total" line has an amount, so skipping it is reported rather than hidden.
        Assert.Contains("\"Total\"", Assert.Single(file.Problems));
    }

    [Fact]
    public void A_debit_and_credit_column_csv_is_detected_and_read()
    {
        const string csv = "Date,Description,Debit,Credit\n2026-09-12,QUICKMART,8.42,\n2026-09-25,PAYROLL,,163.12\n2026-09-26,BAD,,\n";
        var mapping = CsvParser.Detect(CsvParser.FindHeader(CsvParser.ReadRecords(csv))!.Value.Headers);
        Assert.True(mapping!.UsesTwoColumns);
        var file = CsvParser.Parse(csv, mapping);
        Assert.Equal([-842L, 16312L], file.Rows.Select(r => r.AmountCents));
        Assert.Single(file.Problems); // the row with no amount is listed, not dropped silently
    }

    [Fact]
    public void Inverting_the_sign_handles_card_exports_that_show_charges_as_positive()
    {
        const string csv = "Date,Description,Amount\n09/12/2026,AMAZON,25.00\n";
        var mapping = CsvParser.Detect(CsvParser.FindHeader(CsvParser.ReadRecords(csv))!.Value.Headers)! with { InvertSign = true };
        Assert.Equal(-2500, Assert.Single(CsvParser.Parse(csv, mapping).Rows).AmountCents);
    }

    [Fact]
    public void A_credit_union_style_export_with_unquoted_commas_and_times_is_read()
    {
        // Made-up rows in Example CU's layout: newest first, dates with a time, and a description comma left unquoted.
        const string csv = "Date,Transaction Description,Amount,Balance,Check/Misc.,Note,Category\r\n"
            + "9/25/2026 12:00:00 AM,ACH Deposit From EXAMPLE EMPLOYER, INC.,163.12,188.12,,,Uncategorized\r\n"
            + "9/23/2026 12:00:00 AM,Prenotification ACH Deposit From EXAMPLE EMPLOYER, INC.,0.00,25.00,,,Uncategorized\r\n"
            + "9/21/2026 12:00:00 AM,Spotify P123456AB1,-11.99,25.00,,FSD,Utility\r\n";
        var mapping = CsvParser.Detect(CsvParser.FindHeader(CsvParser.ReadRecords(csv))!.Value.Headers)!;
        Assert.Equal(("Date", "Transaction Description", "Amount", "Note"), (mapping.Date, mapping.Description, mapping.Amount, mapping.Memo));

        var file = CsvParser.Parse(csv, mapping);
        Assert.Equal(
            [(D(9, 25), 16312L, "ACH Deposit From EXAMPLE EMPLOYER, INC.", (string?)null), (D(9, 21), -1199L, "Spotify P123456AB1", "FSD")],
            file.Rows.Select(r => (r.Date, r.AmountCents, r.Description, r.Memo)));
        Assert.Contains("$0 notice", Assert.Single(file.Problems)); // the prenote is mentioned, not imported
    }

    [Fact]
    public void A_remembered_layout_fits_only_files_that_have_its_columns()
    {
        var remembered = new CsvMapping("Date", "Description", Amount: "Amount");
        Assert.True(CsvParser.Fits(remembered, ["date", "Description", "Amount", "Balance"]));
        Assert.False(CsvParser.Fits(remembered, ["Date", "Transaction Description", "Amount"])); // the bank renamed a column
        Assert.False(CsvParser.Fits(remembered with { Memo = "Note" }, ["Date", "Description", "Amount"]));
    }

    [Theory]
    [InlineData("POS PURCHASE QUICKMART 0412 RIVERTON PA", "Quickmart Riverton")]
    [InlineData("DEBIT CARD PURCHASE XXXX1234 VALUMART MARKETS #37", "Valumart Markets")]
    [InlineData("ACH DEBIT EXAMPLE CU LOAN PMT", "Example Cu Loan Pmt")]
    [InlineData("SQ *JOES COFFEE", "Joes Coffee")]
    [InlineData("CVS/PHARMACY 01234", "CVS/Pharmacy")]
    [InlineData("12345", "12345")]
    [InlineData("Spotify P123456AB1", "Spotify")]
    [InlineData("EXAMPLEOPTICS* NO1234567", "Exampleoptics")]
    [InlineData("TARGET T-1234", "Target")]
    [InlineData("Transfer from Savings (S0001) (1234…)", "Transfer From Savings")]
    [InlineData("EXAMPLE STORE RETURN", "Example Store")]
    [InlineData("REFUND TARGET T-1234", "Target")]
    [InlineData("Withdrawal FRESHCO #078 Date 09/24/2026", "Freshco")]
    [InlineData("Withdrawal POS WM SUPERCENTER #1001 12 MAIN ST SPRINGFIELD PA", "Wm Supercenter")]
    [InlineData("Withdrawal KALAHARI RESTAURANT - Date 08/30/2026", "Kalahari Restaurant")]
    [InlineData("Deposit Mobile From Share 0001", "Transfer From Share 0001")]
    [InlineData("Withdrawal Mobile To Share 0002", "Transfer To Share 0002")]
    [InlineData("Deposit Online Banking From Pat Q Example 1234XXXXXX Share 0004", "Pat Q Example")]
    [InlineData("Deposit Online Banking From 1234XXXXXX Share 0001", "Transfer From Another Member")]
    [InlineData("August 2026 Dividend APY Earned 2.700%", "Dividend APY Earned")]
    [InlineData("7-ELEVEN 12345", "7-Eleven")]
    public void Bank_descriptions_become_readable_payees(string raw, string expected) => Assert.Equal(expected, PayeeCleaner.Clean(raw));
}

public class ImportServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required ImportService Import { get; init; }
        public required TransactionService Transactions { get; init; }
        public required Account Checking { get; init; }
        public required Account Card { get; init; }
        public required int Groceries { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static IReadOnlyList<ImportDecision> AcceptAll(ImportPreview p) =>
            p.Rows.Where(r => r.IncludeByDefault).Select(r => new ImportDecision(r.Index, r.Type, r.Payee, r.CategoryId, r.TransferAccountId)).ToList();
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        int groceries;
        await using (var ctx = db.Context()) groceries = await ctx.Categories.Where(c => c.Name == "Groceries").Select(c => c.Id).FirstAsync();
        return new Fixture
        {
            Db = db,
            Import = new ImportService(db.Factory, db.Time),
            Transactions = new TransactionService(db.Factory, db.Time),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000),
            Card = await db.AddAccountAsync("Card", AccountType.CreditCard),
            Groceries = groceries,
        };
    }

    private static ParsedFile File(params ImportedRow[] rows) => new(rows, [], "ACCT-1");

    [Fact]
    public async Task Rows_become_suggested_spending_and_income_and_nothing_is_saved_by_previewing()
    {
        await using var f = await CreateAsync();
        var preview = await f.Import.PreviewAsync(f.Checking.Id, File(
            new ImportedRow(D(9, 12), -842, "POS PURCHASE QUICKMART 0412 RIVERTON PA", ExternalId: "1"),
            new ImportedRow(D(9, 25), 16312, "RIVERSIDE PAYROLL", ExternalId: "2")));

        Assert.Equal([(TransactionType.Expense, "Quickmart Riverton"), (TransactionType.Income, "Riverside Payroll")], preview.Rows.Select(r => (r.Type, r.Payee)));
        Assert.Equal(2, preview.NewCount);
        await using var db = f.Db.Context();
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Importing_saves_one_batch_and_the_same_file_twice_adds_nothing()
    {
        await using var f = await CreateAsync();
        var file = File(new ImportedRow(D(9, 12), -842, "QUICKMART", ExternalId: "1"), new ImportedRow(D(9, 25), 16312, "PAYROLL", ExternalId: "2"));

        var first = await f.Import.CommitAsync(f.Checking.Id, file, Fixture.AcceptAll(await f.Import.PreviewAsync(f.Checking.Id, file)), "credit_union.qfx");
        Assert.Equal((2, 0), (first.Imported, first.SkippedAsAlreadyImported));

        var again = await f.Import.PreviewAsync(f.Checking.Id, file);
        Assert.All(again.Rows, r => Assert.Equal(DuplicateKind.AlreadyImported, r.Duplicate));
        // Even forcing every row through is refused per row, never duplicated.
        var forced = again.Rows.Select(r => new ImportDecision(r.Index, r.Type, r.Payee, r.CategoryId)).ToList();
        Assert.Equal((0, 2), ((await f.Import.CommitAsync(f.Checking.Id, file, forced, "credit_union.qfx")).Imported, 2));

        var all = await f.Transactions.QueryAsync(new TransactionQuery());
        Assert.Equal(2, all.Count);
        Assert.Equal(50000 - 842 + 16312, await new AccountService(f.Db.Factory, f.Db.Time).GetBalanceAsync(f.Checking.Id));
        Assert.Equal(f.Checking.Id, await f.Import.FindAccountForBankKeyAsync("ACCT-1"));
    }

    [Fact]
    public async Task Something_already_typed_in_by_hand_is_flagged_likely_and_matched_only_once()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 500, D(9, 11), f.Checking.Id, Payee: "Joe's Coffee"));

        var preview = await f.Import.PreviewAsync(f.Checking.Id, File(
            new ImportedRow(D(9, 12), -500, "SQ *JOES COFFEE"),       // a day later, same payee: likely the one typed in
            new ImportedRow(D(9, 12), -500, "SQ *JOES COFFEE"),       // a second coffee: new (the typed one is already matched)
            new ImportedRow(D(9, 20), -500, "SQ *JOES COFFEE"),       // too far apart: new
            new ImportedRow(D(9, 12), 500, "SQ *JOES COFFEE REFUND")  // money in, not out: new
        ));

        Assert.Equal([DuplicateKind.Likely, DuplicateKind.None, DuplicateKind.None, DuplicateKind.None], preview.Rows.Select(r => r.Duplicate));
        Assert.Contains("Joe's Coffee on Sep 11", preview.Rows[0].DuplicateText);
        Assert.False(preview.Rows[0].IncludeByDefault);
    }

    [Fact]
    public async Task Money_into_a_card_is_suggested_as_a_payment_transfer_and_imports_as_one()
    {
        await using var f = await CreateAsync();
        var file = File(new ImportedRow(D(9, 20), 5000, "PAYMENT THANK YOU", ExternalId: "p1"), new ImportedRow(D(9, 21), -2500, "AMAZON", ExternalId: "c1"));
        var preview = await f.Import.PreviewAsync(f.Card.Id, file);
        Assert.Equal(TransactionType.Transfer, preview.Rows[0].Type);
        Assert.NotNull(preview.Rows[0].AttentionText);

        var decisions = new[]
        {
            new ImportDecision(0, TransactionType.Transfer, "Card payment", null, TransferAccountId: f.Checking.Id),
            new ImportDecision(1, TransactionType.Expense, "Amazon", null),
        };
        await f.Import.CommitAsync(f.Card.Id, file, decisions, "card.csv");

        var accounts = new AccountService(f.Db.Factory, f.Db.Time);
        Assert.Equal(50000 - 5000, await accounts.GetBalanceAsync(f.Checking.Id));   // money left checking
        Assert.Equal(5000 - 2500, await accounts.GetBalanceAsync(f.Card.Id));        // paid down, then a charge
        var totals = await f.Transactions.GetTotalsAsync(new TransactionQuery());
        Assert.Equal((0L, 2500L), (totals.IncomeCents, totals.ExpenseCents));          // the payment is neither
    }

    [Fact]
    public async Task Transfers_between_your_own_accounts_are_suggested_remembered_and_not_imported_twice()
    {
        await using var f = await CreateAsync();
        var savings = await f.Db.AddAccountAsync("Savings", AccountType.Savings, 70000);

        // Checking's file: the description names the Savings account, so it's suggested as a transfer from it.
        var checkingFile = File(
            new ImportedRow(D(9, 17), 12000, "Transfer from Main Savings (S0001) (1234…)"),
            new ImportedRow(D(9, 18), 2000, "Transfer from Pat Example S0004 (9999…)"));
        var preview = await f.Import.PreviewAsync(f.Checking.Id, checkingFile);
        Assert.Equal((TransactionType.Transfer, (int?)savings.Id), (preview.Rows[0].Type, preview.Rows[0].TransferAccountId));
        Assert.Equal((TransactionType.OutsideIn, (int?)null), (preview.Rows[1].Type, preview.Rows[1].TransferAccountId));
        Assert.NotNull(preview.Rows[1].AttentionText); // someone else's account: neither income nor spending, with a hint

        await f.Import.CommitAsync(f.Checking.Id, checkingFile,
        [
            new ImportDecision(0, TransactionType.Transfer, "Transfer From Savings", null, savings.Id, RememberPayee: true),
            new ImportDecision(1, TransactionType.Income, "Pat", null, RememberPayee: true),
        ], "checking.csv");
        var accounts = new AccountService(f.Db.Factory, f.Db.Time);
        Assert.Equal((50000L + 12000 + 2000, 70000L - 12000), (await accounts.GetBalanceAsync(f.Checking.Id), await accounts.GetBalanceAsync(savings.Id)));

        // Savings' file has the other side of the same transfer: already in Ledger, so it starts unticked.
        // A different withdrawal of the same amount that day, listed first, must not claim it.
        var savingsPreview = await f.Import.PreviewAsync(savings.Id, File(
            new ImportedRow(D(9, 17), -12000, "Member-to-Member Transfer Sent to Pat Example"),
            new ImportedRow(D(9, 17), -12000, "Transfer to Checking (S0002) (1234…)")));
        Assert.Equal([DuplicateKind.None, DuplicateKind.Likely], savingsPreview.Rows.Select(r => r.Duplicate));

        // Next month, the remembered rules decide: the transfer keeps its account, the other stays income.
        var next = await f.Import.PreviewAsync(f.Checking.Id, File(
            new ImportedRow(D(10, 17), 5000, "Transfer from Main Savings (S0001) (1234…)"),
            new ImportedRow(D(10, 18), 1000, "Transfer from Pat Example S0004 (9999…)")));
        Assert.Equal((TransactionType.Transfer, (int?)savings.Id, true), (next.Rows[0].Type, next.Rows[0].TransferAccountId, next.Rows[0].FromRememberedRule));
        Assert.Equal((TransactionType.Income, "Pat", (string?)null), (next.Rows[1].Type, next.Rows[1].Payee, next.Rows[1].AttentionText));
    }

    [Fact]
    public async Task Money_back_from_a_store_you_spend_at_is_suggested_as_a_refund_not_income()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 4000, D(9, 1), f.Checking.Id, CategoryId: f.Groceries, Payee: "Target"));
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 2500, D(9, 2), f.Card.Id, CategoryId: f.Groceries, Payee: "Amazon"));

        var file = File(
            new ImportedRow(D(9, 20), 1500, "TARGET T-1234", ExternalId: "r1"),        // spent there before
            new ImportedRow(D(9, 21), 999, "EXAMPLE STORE RETURN", ExternalId: "r2"),  // says so
            new ImportedRow(D(9, 22), 16312, "ACH DEPOSIT EXAMPLE PAYROLL", ExternalId: "r3"));
        var preview = await f.Import.PreviewAsync(f.Checking.Id, file);
        Assert.Equal([TransactionType.Refund, TransactionType.Refund, TransactionType.Income], preview.Rows.Select(r => r.Type));
        Assert.Equal((int?)f.Groceries, preview.Rows[0].CategoryId);

        await f.Import.CommitAsync(f.Checking.Id, file, Fixture.AcceptAll(preview), "a.csv");
        var totals = await f.Transactions.GetTotalsAsync(new TransactionQuery(From: D(9, 1), To: Today));
        Assert.Equal((16312L, 4000L + 2500 - 1500 - 999, 1500L + 999), (totals.IncomeCents, totals.ExpenseCents, totals.RefundCents));

        // On a card, money back from a store is a refund; a payment with no store history stays a payment.
        var card = await f.Import.PreviewAsync(f.Card.Id, File(new ImportedRow(D(9, 23), 2500, "AMAZON"), new ImportedRow(D(9, 24), 5000, "PAYMENT THANK YOU")));
        Assert.Equal([TransactionType.Refund, TransactionType.Transfer], card.Rows.Select(r => r.Type));
    }

    [Fact]
    public async Task A_bank_row_that_pays_a_bill_is_linked_so_the_bill_counts_as_recorded()
    {
        await using var f = await CreateAsync();
        var recurring = new RecurringService(f.Db.Factory, f.Db.Time);
        var spotify = await recurring.CreateAsync(new RecurringInput("Spotify", TransactionType.Expense, 1199, RecurrenceFrequency.Monthly, D(8, 21), f.Checking.Id, CategoryId: f.Groceries));
        var electric = await recurring.CreateAsync(new RecurringInput("Electric", TransactionType.Expense, 9000, RecurrenceFrequency.Monthly, D(8, 25), f.Checking.Id, IsAmountEstimated: true));

        var file = File(
            new ImportedRow(D(9, 22), -1199, "Spotify P123456AB1", ExternalId: "b1"),   // a day after it was due
            new ImportedRow(D(9, 26), -9540, "ELECTRIC CO PAYMENT", ExternalId: "b2"),  // estimated bill, amount within 20%
            new ImportedRow(D(9, 26), -5000, "SOMETHING ELSE", ExternalId: "b3"));
        var preview = await f.Import.PreviewAsync(f.Checking.Id, file);
        Assert.Equal((spotify.Id, D(9, 21)), (preview.Rows[0].Bill!.RecurringTransactionId, preview.Rows[0].Bill!.Date));
        Assert.Equal((int?)f.Groceries, preview.Rows[0].CategoryId); // the bill's category fills in
        Assert.Equal((electric.Id, D(9, 25)), (preview.Rows[1].Bill!.RecurringTransactionId, preview.Rows[1].Bill!.Date));
        Assert.Null(preview.Rows[2].Bill);

        var decisions = preview.Rows.Select(r => new ImportDecision(r.Index, r.Type, r.Payee, r.CategoryId, Bill: r.Bill)).ToList();
        await f.Import.CommitAsync(f.Checking.Id, file, decisions, "bills.csv");
        var september = await recurring.GetOccurrencesAsync(D(9, 1), D(9, 30));
        Assert.All(september, o => Assert.Equal(OccurrenceState.Recorded, o.State));
        Assert.Equal(9540, september.Single(o => o.Rule.Id == electric.Id).RecordedCents);

        // Undoing the import puts both bills back to "needs recording".
        await f.Import.UndoAsync((await f.Import.GetRecentBatchesAsync()).Single().Id);
        Assert.All(await recurring.GetOccurrencesAsync(D(9, 1), D(9, 30)), o => Assert.Equal(OccurrenceState.NotRecorded, o.State));
    }

    [Fact]
    public async Task An_imported_paycheck_deposit_counts_as_the_predicted_paycheck()
    {
        await using var f = await CreateAsync();
        var income = new IncomeService(f.Db.Factory, new SettingsService(f.Db.Factory));
        var forecasts = new IncomeForecastService(f.Db.Factory, income);
        var import = new ImportService(f.Db.Factory, f.Db.Time, forecasts);
        var job = await income.CreateJobAsync(new JobInput("Riverside Market", 1350, D(9, 1), PayFrequency.Weekly, D(9, 20), 6,
            false, DayOfWeek.Sunday, true, f.Checking.Id, null, FilingStatus.Single, false, 0, 0, 0, 0, "Riverton City", "990201", 50, 5200, false));
        await income.AddShiftAsync(new ShiftInput(job.Id, D(9, 24), 240, null, null, null, ShiftStatus.Worked, null));
        var predicted = Assert.Single(await forecasts.GetPaychecksAsync(D(9, 1), D(10, 31)), p => p.NetCents > 0);
        Assert.Equal(D(10, 2), predicted.Period.PayDate);

        var file = File(new ImportedRow(D(10, 2), 4838, "RIVERSIDE MARKET, INC. PAYROLL", ExternalId: "k1"), new ImportedRow(D(10, 2), 2000, "VENMO CASHOUT", ExternalId: "v1"));
        var preview = await import.PreviewAsync(f.Checking.Id, file);
        Assert.Equal((job.Id, D(10, 2)), (preview.Rows[0].Paycheck!.JobId, preview.Rows[0].Paycheck!.Period.PayDate));
        Assert.Null(preview.Rows[1].Paycheck); // not the employer, and not near the estimate

        await import.CommitAsync(f.Checking.Id, file, preview.Rows.Select(r => new ImportDecision(r.Index, r.Type, r.Payee, r.CategoryId, Paycheck: r.Paycheck)).ToList(), "a.qfx");
        var recorded = (await forecasts.GetPaychecksAsync(D(9, 1), D(10, 31))).Single(p => p.Period.PayDate == D(10, 2)).Recorded;
        Assert.Equal(4838, recorded!.ActualCents);

        // Undoing the import makes it an estimate again.
        await import.UndoAsync((await import.GetRecentBatchesAsync()).Single().Id);
        Assert.Null((await forecasts.GetPaychecksAsync(D(9, 1), D(10, 31))).Single(p => p.Period.PayDate == D(10, 2)).Recorded);
    }

    [Fact]
    public async Task A_remembered_correction_is_suggested_next_time_and_history_suggests_categories()
    {
        await using var f = await CreateAsync();
        var file1 = File(new ImportedRow(D(9, 12), -842, "POS PURCHASE QUICKMART 0412 RIVERTON PA", ExternalId: "1"));
        await f.Import.CommitAsync(f.Checking.Id, file1,
            [new ImportDecision(0, TransactionType.Expense, "Quickmart", f.Groceries, RememberPayee: true)], "a.qfx");

        var next = await f.Import.PreviewAsync(f.Checking.Id, File(new ImportedRow(D(9, 19), -1234, "POS PURCHASE QUICKMART 0413 RIVERTON PA", ExternalId: "2")));
        var row = Assert.Single(next.Rows);
        Assert.Equal(("Quickmart", (int?)f.Groceries, true), (row.Payee, row.CategoryId, row.FromRememberedRule));

        // Without a rule, the category most used for that payee is suggested.
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 100, D(9, 1), f.Checking.Id, CategoryId: f.Groceries, Payee: "Valumart Markets"));
        var valumart = Assert.Single((await f.Import.PreviewAsync(f.Checking.Id, File(new ImportedRow(D(9, 20), -4000, "VALUMART MARKETS #37")))).Rows);
        Assert.Equal(("Valumart Markets", (int?)f.Groceries), (valumart.Payee, valumart.CategoryId));
    }

    [Fact]
    public async Task A_bad_row_saves_nothing_at_all()
    {
        await using var f = await CreateAsync();
        var file = File(new ImportedRow(D(9, 12), -842, "QUICKMART"), new ImportedRow(D(9, 13), -100, "X"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Import.CommitAsync(f.Checking.Id, file,
        [
            new ImportDecision(0, TransactionType.Expense, "Quickmart", null),
            new ImportDecision(1, TransactionType.Income, "X", null), // money out can't be income
        ], "x.csv"));

        await using var db = f.Db.Context();
        Assert.Equal(0, await db.Transactions.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.ImportBatches.CountAsync());
    }

    [Fact]
    public async Task Undo_removes_exactly_that_import_and_the_file_can_be_imported_again()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 999, D(9, 1), f.Checking.Id, Payee: "Typed by hand"));
        var file = File(new ImportedRow(D(9, 12), -842, "QUICKMART", ExternalId: "1"));
        var result = await f.Import.CommitAsync(f.Checking.Id, file, Fixture.AcceptAll(await f.Import.PreviewAsync(f.Checking.Id, file)), "a.qfx");

        Assert.Equal(1, await f.Import.UndoAsync(result.BatchId));
        Assert.Equal(0, await f.Import.UndoAsync(result.BatchId)); // twice is harmless
        Assert.Equal(["Typed by hand"], (await f.Transactions.QueryAsync(new TransactionQuery())).Select(t => t.Payee));

        var again = await f.Import.PreviewAsync(f.Checking.Id, file);
        Assert.Equal(DuplicateKind.None, Assert.Single(again.Rows).Duplicate);
    }

    [Fact]
    public async Task The_csv_layout_is_remembered_per_account()
    {
        await using var f = await CreateAsync();
        var mapping = new CsvMapping("Posted Date", "Description", Amount: "Amount", InvertSign: true);
        var file = File(new ImportedRow(D(9, 12), -842, "QUICKMART"));
        await f.Import.CommitAsync(f.Card.Id, file, Fixture.AcceptAll(await f.Import.PreviewAsync(f.Card.Id, file)), "card.csv", mapping);
        Assert.Equal(mapping, await f.Import.GetCsvMappingAsync(f.Card.Id));
        Assert.Null(await f.Import.GetCsvMappingAsync(f.Checking.Id));
    }
}
