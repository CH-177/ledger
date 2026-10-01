using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Services;
using Ledger.Core;
using Ledger.Core.Import;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

public sealed record RecentImportViewModel(int BatchId, string Title, string Detail, bool CanUndo)
{
    public string UndoName => $"Undo import {Title}";
}

/// <summary>
/// Importing a bank file: choose it, pick the account (and, for CSV, which column is which),
/// review every row, then import them together. Nothing is saved before "Import".
/// </summary>
public sealed partial class ImportViewModel(
    ImportService import,
    AccountService accounts,
    CategoryService categories,
    FilePickerService pickers,
    NavigationService navigation,
    ILogger<ImportViewModel> logger) : ObservableObject
{
    private const string NoColumn = "(none)";
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    private string _text = "";
    private string _fileName = "";
    private bool _isCsv;
    private ParsedFile? _parsed;
    private bool _loading;
    private List<FilterOption<int?>> _spending = [];
    private List<FilterOption<int?>> _income = [];

    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<string> HeaderOptions { get; } = [];
    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];
    public ObservableCollection<RecentImportViewModel> Recent { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowIntro))] public partial bool HasFile { get; set; }
    [ObservableProperty] public partial string FileTitle { get; set; } = "";
    [ObservableProperty] public partial bool IsCsv { get; set; }
    [ObservableProperty] public partial int AccountIndex { get; set; } = -1;

    [ObservableProperty] public partial int DateIndex { get; set; }
    [ObservableProperty] public partial int DescriptionIndex { get; set; }
    [ObservableProperty] public partial int AmountIndex { get; set; }
    [ObservableProperty] public partial int DebitIndex { get; set; }
    [ObservableProperty] public partial int CreditIndex { get; set; }
    [ObservableProperty] public partial int MemoIndex { get; set; }
    [ObservableProperty] public partial int ReferenceIndex { get; set; }
    [ObservableProperty] public partial int BalanceIndex { get; set; }

    /// <summary>The bank's balance from the file compared with Ledger's, when the file has one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBankBalance))] public partial string? BankBalanceText { get; set; }
    [ObservableProperty] public partial bool CanMatchBank { get; set; }
    public bool HasBankBalance => BankBalanceText is not null;
    private BankBalance? _bank;
    private int? _bankAccountId;
    [ObservableProperty] public partial bool InvertSign { get; set; }

    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial bool HasProblems { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string ImportButtonText { get; set; } = "Import";
    [ObservableProperty] public partial bool CanImport { get; set; }
    [ObservableProperty] public partial bool HasRecent { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult))] public partial string? ResultText { get; set; }
    public int? LastBatchId { get; private set; }
    public bool HasResult => ResultText is not null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string? ErrorMessage { get; set; }
    public bool HasError => ErrorMessage is not null;
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool ShowIntro => !HasFile;

    public async Task LoadAsync()
    {
        try
        {
            var open = (await accounts.GetAccountBalancesAsync()).Select(b => b.Account).ToList();
            AccountOptions.Clear();
            foreach (var a in open) AccountOptions.Add(new(a.Name, a.Id));
            await LoadCategoriesAsync();
            await LoadRecentAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load import page");
            ErrorMessage = "Ledger couldn't open the import page. Your data hasn't been changed.";
        }
    }

    private async Task LoadCategoriesAsync()
    {
        var tree = await categories.GetTreeAsync();
        List<FilterOption<int?>> For(CategoryType type)
        {
            var list = new List<FilterOption<int?>> { new("No category", null) };
            foreach (var node in tree.Where(n => n.Category.CategoryType == type))
            {
                list.Add(new(node.Category.Name, node.Category.Id));
                list.AddRange(node.Children.Select(c => new FilterOption<int?>($"{node.Category.Name} › {c.Name}", c.Id)));
            }
            return list;
        }
        _spending = For(CategoryType.Expense);
        _income = For(CategoryType.Income);
    }

    private async Task LoadRecentAsync()
    {
        Recent.Clear();
        foreach (var b in await import.GetRecentBatchesAsync())
        {
            Recent.Add(new RecentImportViewModel(b.Id, b.FileName,
                $"{b.TransactionCount} transaction(s) into {b.Account?.Name}, {b.ImportedAt.ToLocalTime().ToString("MMM d, h:mm tt", Culture)}" + (b.IsUndone ? "  ·  undone" : ""),
                !b.IsUndone && b.TransactionCount > 0));
        }
        HasRecent = Recent.Count > 0;
    }

    // ---- Choosing the file -----------------------------------------------------------------------

    public async Task ChooseFileAsync()
    {
        ErrorMessage = null;
        ResultText = null;
        BankBalanceText = null;
        CanMatchBank = false;
        var path = await pickers.PickOpenAsync(".csv", ".qfx", ".ofx");
        if (path is null) return;

        try
        {
            _text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't read import file");
            ErrorMessage = "Ledger couldn't read that file. If it's open in another program, close it and try again.";
            return;
        }
        _fileName = Path.GetFileName(path);
        _isCsv = !QfxParser.LooksLikeOfx(_text);
        IsCsv = _isCsv;
        FileTitle = $"{_fileName} ({(_isCsv ? "CSV" : "QFX")})";

        _loading = true;
        try
        {
            if (_isCsv)
            {
                if (CsvParser.FindHeader(CsvParser.ReadRecords(_text)) is not { } header)
                {
                    ErrorMessage = "This CSV has no header row with a date column, so Ledger can't tell which column is which.";
                    return;
                }
                HeaderOptions.Clear();
                HeaderOptions.Add(NoColumn);
                foreach (var h in header.Headers.Where(h => h.Length > 0)) HeaderOptions.Add(h);
                _parsed = null;
            }
            else
            {
                _parsed = QfxParser.Parse(_text);
            }

            // Pick the account: the one this bank account id went into last time, if known.
            var known = _parsed is null ? null : await import.FindAccountForBankKeyAsync(_parsed.BankAccountKey);
            AccountIndex = known is { } id ? AccountOptions.ToList().FindIndex(o => o.Value == id) : AccountOptions.Count == 1 ? 0 : -1;
            if (_isCsv) await ApplyMappingForAccountAsync();
        }
        finally
        {
            _loading = false;
        }
        HasFile = true;
        await RefreshAsync();
    }

    /// <summary>Uses this account's remembered layout, or guesses one from the headers.</summary>
    private async Task ApplyMappingForAccountAsync()
    {
        var headers = HeaderOptions.Skip(1).ToArray();
        var accountId = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        // A remembered layout only counts if this file has its columns (the bank may rename one, or
        // this may be a different kind of file); otherwise guess again from the headers.
        var remembered = accountId is { } id ? await import.GetCsvMappingAsync(id) : null;
        var mapping = remembered is not null && CsvParser.Fits(remembered, headers) ? remembered : CsvParser.Detect(headers) ?? remembered;
        int Index(string? name) => name is null ? 0 : Math.Max(0, HeaderOptions.ToList().FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)));
        DateIndex = Index(mapping?.Date);
        DescriptionIndex = Index(mapping?.Description);
        AmountIndex = Index(mapping?.Amount);
        DebitIndex = Index(mapping?.Debit);
        CreditIndex = Index(mapping?.Credit);
        MemoIndex = Index(mapping?.Memo);
        ReferenceIndex = Index(mapping?.Reference);
        BalanceIndex = Index(mapping?.Balance);
        InvertSign = mapping?.InvertSign ?? false;
    }

    private CsvMapping? CurrentMapping()
    {
        string? Column(int i) => i > 0 ? HeaderOptions.ElementAtOrDefault(i) : null;
        return Column(DateIndex) is { } date && Column(DescriptionIndex) is { } description
               && (Column(AmountIndex) is not null || Column(DebitIndex) is not null || Column(CreditIndex) is not null)
            ? new CsvMapping(date, description, Column(AmountIndex), Column(AmountIndex) is null ? Column(DebitIndex) : null,
                Column(AmountIndex) is null ? Column(CreditIndex) : null, Column(MemoIndex), Column(ReferenceIndex), null, InvertSign, Column(BalanceIndex))
            : null;
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || !HasFile) return;
        if (e.PropertyName == nameof(AccountIndex) && _isCsv)
        {
            _loading = true;
            try { await ApplyMappingForAccountAsync(); } finally { _loading = false; }
        }
        if (e.PropertyName is nameof(AccountIndex) or nameof(DateIndex) or nameof(DescriptionIndex) or nameof(AmountIndex)
            or nameof(DebitIndex) or nameof(CreditIndex) or nameof(MemoIndex) or nameof(ReferenceIndex) or nameof(InvertSign)
            or nameof(BalanceIndex))
        {
            await RefreshAsync();
        }
    }

    // ---- Review -----------------------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        Problems.Clear();
        HasRows = false;
        HasProblems = false;

        var accountId = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        if (accountId is null)
        {
            SummaryText = "Choose which account this file is for.";
            UpdateCounts();
            return;
        }
        if (_isCsv)
        {
            if (CurrentMapping() is not { } mapping)
            {
                SummaryText = "Choose the date, description and amount columns.";
                UpdateCounts();
                return;
            }
            _parsed = CsvParser.Parse(_text, mapping);
        }

        try
        {
            IsBusy = true;
            var preview = await import.PreviewAsync(accountId.Value, _parsed!);
            var others = AccountOptions.Where(o => o.Value != accountId).Prepend(new FilterOption<int?>("Choose an account", null))
                .Append(new FilterOption<int?>("Someone else's account (outside)", TransactionEditorViewModel.OutsideAccount)).ToList();
            foreach (var c in preview.Rows)
            {
                var row = new ImportRowViewModel(c, _spending, _income, others);
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }
            foreach (var p in preview.Problems) Problems.Add(p);
            HasRows = Rows.Count > 0;
            HasProblems = Problems.Count > 0;
            await UpdateBankBalanceAsync(accountId.Value);
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import preview failed");
            ErrorMessage = "Ledger couldn't read the rows in this file.";
        }
        finally
        {
            IsBusy = false;
        }
        UpdateCounts();
    }

    /// <summary>
    /// Compares the bank's balance at the end of the file's last day with Ledger's for that day. The
    /// bank's number is the truth: if they differ (typically because the starting balance already
    /// included transactions now imported), the user can make Ledger use it.
    /// </summary>
    private async Task UpdateBankBalanceAsync(int accountId)
    {
        BankBalanceText = null;
        CanMatchBank = false;
        _bank = _parsed?.EndingBalance;
        _bankAccountId = accountId;
        if (_bank is not { } bank) return;
        var account = (await accounts.GetAccountBalancesAsync(includeArchived: true)).FirstOrDefault(b => b.Account.Id == accountId)?.Account;
        if (account is null || account.IsLiability) return; // a card's balance sign varies by bank
        var ledger = await accounts.GetBalanceAsync(accountId, bank.Date);
        var day = bank.Date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));
        if (ledger == bank.BalanceCents)
        {
            BankBalanceText = $"Ledger matches your bank: {account.Name} had {Money.Format(bank.BalanceCents)} at the end of {day}.";
            return;
        }
        BankBalanceText = $"Your bank says {account.Name} had {Money.Format(bank.BalanceCents)} at the end of {day}, but Ledger shows {Money.Format(ledger)} for that day. " +
                          "If the starting balance you entered already included some of these transactions, use the bank's balance: " +
                          $"everything on or before {day} is then counted as already in it. (Anything you entered by hand that the bank " +
                          "hadn't posted by then isn't in the bank's number yet.)";
        CanMatchBank = true;
    }

    /// <summary>Makes the account's balance at the end of the file's last day the bank's.</summary>
    public async Task MatchBankBalanceAsync()
    {
        if (_bank is not { } bank || _bankAccountId is not { } accountId) return;
        ErrorMessage = null;
        try
        {
            await accounts.SetBalanceFromBankAsync(accountId, bank.Date, bank.BalanceCents);
            await UpdateBankBalanceAsync(accountId);
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Matching the bank balance failed");
            ErrorMessage = "Something went wrong, so the balance wasn't changed.";
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportRowViewModel.Include)) UpdateCounts();
    }

    private void UpdateCounts()
    {
        var ticked = Rows.Count(r => r.Include);
        var duplicates = Rows.Count(r => r.IsDuplicate);
        if (Rows.Count > 0)
        {
            SummaryText = $"{Rows.Count} row(s) in the file  ·  {ticked} ticked to import" +
                          (duplicates > 0 ? $"  ·  {duplicates} probably already in Ledger (unticked)" : "");
        }
        ImportButtonText = ticked == 1 ? "Import 1 transaction" : $"Import {ticked} transactions";
        CanImport = ticked > 0 && !IsBusy;
    }

    public void SetAll(bool include)
    {
        foreach (var r in Rows) r.Include = include;
    }

    public async Task ImportAsync()
    {
        ErrorMessage = null;
        var decisions = new List<ImportDecision>();
        foreach (var row in Rows.Where(r => r.Include))
        {
            var (decision, problem) = row.ToDecision();
            if (problem is not null)
            {
                ErrorMessage = problem;
                return;
            }
            decisions.Add(decision!);
        }
        var accountId = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        if (accountId is null || _parsed is null) return;

        IsBusy = true;
        try
        {
            var result = await import.CommitAsync(accountId.Value, _parsed, decisions, _fileName, _isCsv ? CurrentMapping() : null);
            LastBatchId = result.BatchId;
            var account = AccountOptions.First(o => o.Value == accountId).Label;
            ResultText = $"Imported {result.Imported} transaction{(result.Imported == 1 ? "" : "s")} into {account}." +
                         (result.SkippedAsAlreadyImported > 0 ? $" {result.SkippedAsAlreadyImported} were already there and were skipped." : "");
            HasFile = false;
            foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            Problems.Clear();
            HasRows = HasProblems = false;
            await LoadRecentAsync();
            if (_bankAccountId is { } bankAccount) await UpdateBankBalanceAsync(bankAccount);
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed");
            ErrorMessage = "Something went wrong, so nothing was imported. Your data hasn't been changed.";
        }
        finally
        {
            IsBusy = false;
            UpdateCounts();
        }
    }

    public async Task UndoAsync(int batchId)
    {
        ErrorMessage = null;
        try
        {
            var removed = await import.UndoAsync(batchId);
            ResultText = $"Import undone: {removed} transaction{(removed == 1 ? "" : "s")} removed. You can import the file again.";
            if (LastBatchId == batchId) LastBatchId = null;
            await LoadRecentAsync();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Undo import failed");
            ErrorMessage = "Ledger couldn't undo that import. Your data hasn't been changed.";
        }
    }

    public void Cancel()
    {
        HasFile = false;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        Problems.Clear();
        HasRows = HasProblems = false;
        ErrorMessage = null;
        BankBalanceText = null;
        CanMatchBank = false;
    }

    public void GoToTransactions() => navigation.Navigate(AppPage.Transactions);
}
