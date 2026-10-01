using Ledger.Core.Import;
using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class OutsideTransferTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    [Fact]
    public async Task A_transfer_with_someone_elses_account_moves_the_balance_but_is_neither_income_nor_spending()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var tx = new TransactionService(db.Factory, db.Time);
        var accounts = new AccountService(db.Factory, db.Time);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 10000);
        await tx.CreateAsync(new TransactionInput(TransactionType.OutsideIn, 1500, D(9, 10), checking.Id, Payee: "Family"));
        await tx.CreateAsync(new TransactionInput(TransactionType.OutsideOut, 2000, D(9, 19), checking.Id, Payee: "Family"));
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 500, D(9, 20), checking.Id));

        Assert.Equal(10000 + 1500 - 2000 - 500, await accounts.GetBalanceAsync(checking.Id));
        var totals = await tx.GetTotalsAsync(new TransactionQuery());
        Assert.Equal((3, 0L, 500L), (totals.Count, totals.IncomeCents, totals.ExpenseCents));

        // "Transfers" lists them; their signed amounts follow the money.
        var transfers = await tx.QueryAsync(new TransactionQuery(Type: TransactionType.Transfer));
        Assert.Equal([-2000L, 1500L], transfers.Select(t => t.SignedAmountFor(null)!.Value));

        // They can't have another Ledger account on the other side, or be scheduled.
        await Assert.ThrowsAsync<LedgerValidationException>(() => new RecurringService(db.Factory, db.Time).CreateAsync(
            new RecurringInput("Allowance", TransactionType.OutsideIn, 1000, RecurrenceFrequency.Monthly, D(10, 1), checking.Id)));
    }

    [Fact]
    public async Task Credit_union_qfx_transfers_are_told_apart_by_share_and_member_number()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var import = new ImportService(db.Factory, db.Time);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 10000);
        var savings = await db.AddAccountAsync("Savings", AccountType.Savings, 50000);
        ParsedFile File(params ImportedRow[] rows) => new(rows, [], "123S0002");
        var rows = new[]
        {
            new ImportedRow(D(9, 3), 1200, "Deposit Mobile From Share 0001"),
            new ImportedRow(D(9, 10), 2000, "Deposit Online Banking From Pat Q Example 1234XXXXXX Share 0004"),
        };

        // Before Savings' own file has been imported, Share 0001 is one of yours but Ledger can't tell which.
        var first = await import.PreviewAsync(checking.Id, File(rows));
        Assert.Equal((TransactionType.Transfer, (int?)null), (first.Rows[0].Type, first.Rows[0].TransferAccountId));
        Assert.Contains("Share 0001", first.Rows[0].AttentionText);
        Assert.Equal(TransactionType.OutsideIn, first.Rows[1].Type); // another member's number: someone else's

        // Once Savings' file (account 123S0001) has been imported, Share 0001 is known to be Savings.
        await import.CommitAsync(savings.Id, new ParsedFile([new ImportedRow(D(9, 1), 172, "Dividend")], [], "123S0001"),
            [new ImportDecision(0, TransactionType.Income, "Dividend", null)], "savings.qfx");
        var second = await import.PreviewAsync(checking.Id, File(rows));
        Assert.Equal((TransactionType.Transfer, (int?)savings.Id), (second.Rows[0].Type, second.Rows[0].TransferAccountId));
    }

    [Fact]
    public async Task Import_suggests_outside_for_other_peoples_transfers_and_remembers_the_choice()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var import = new ImportService(db.Factory, db.Time);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 10000);
        var file = new ParsedFile([
            new ImportedRow(D(9, 10), 1500, "Transfer from Pat Example S0004 (9999…)"),
            new ImportedRow(D(9, 19), -2000, "Member-to-Member Transfer Sent to Pat Example"),
        ], []);
        var preview = await import.PreviewAsync(checking.Id, file);
        Assert.Equal([TransactionType.OutsideIn, TransactionType.OutsideOut], preview.Rows.Select(r => r.Type));

        await import.CommitAsync(checking.Id, file, preview.Rows.Select(r => new ImportDecision(r.Index, r.Type, "Pat", null, RememberPayee: true)).ToList(), "a.csv");
        Assert.Equal(10000 + 1500 - 2000, await new AccountService(db.Factory, db.Time).GetBalanceAsync(checking.Id));

        var next = await import.PreviewAsync(checking.Id, new ParsedFile([new ImportedRow(D(10, 10), 1500, "Transfer from Pat Example S0004 (9999…)")], []));
        var row = Assert.Single(next.Rows);
        Assert.Equal((TransactionType.OutsideIn, true, "Pat"), (row.Type, row.FromRememberedRule, row.Payee));
    }
}
