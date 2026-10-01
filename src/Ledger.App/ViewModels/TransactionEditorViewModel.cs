using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Form state for recording or editing one transaction. Built for fast entry.</summary>
public sealed partial class TransactionEditorViewModel(
    TransactionService transactions,
    AccountService accounts,
    CategoryService categories,
    SettingsService settings,
    SuggestionService suggestions,
    GoalService goals,
    TimeProvider time,
    ILogger<TransactionEditorViewModel> logger) : ObservableObject
{
    private const string LastAccountKey = "Transactions.LastAccountId";
    private static readonly TransactionType[] TypeOrder = [TransactionType.Expense, TransactionType.Income, TransactionType.Transfer, TransactionType.Refund];

    /// <summary>The "Someone else's account" choice in the To account list (never a real account id).</summary>
    public const int OutsideAccount = -1;

    private int? _editingId;
    private Transaction? _original;
    private IReadOnlyList<CategoryNode> _tree = [];
    private IReadOnlyList<Account> _accounts = [];

    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> ToAccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> CategoryOptions { get; } = [];
    public ObservableCollection<PayeeSuggestion> PayeeSuggestions { get; } = [];
    public ObservableCollection<string> NoteSuggestions { get; } = [];

    /// <summary>"Not from a fund", then each sinking fund with what it has set aside.</summary>
    public ObservableCollection<FilterOption<int?>> FundOptions { get; } = [];
    [ObservableProperty] public partial int FundIndex { get; set; }
    private Dictionary<int, int> _fundByCategory = [];
    private bool _fundChosenByUser;
    private bool _settingFund;
    public bool ShowFunds => SelectedType == TransactionType.Expense && FundOptions.Count > 1;

    partial void OnFundIndexChanged(int value)
    {
        if (!_settingFund) _fundChosenByUser = true;
    }

    // Spending in a category a fund pays for is offered as paid from that fund (visible, and easy to change).
    partial void OnCategoryIndexChanged(int value)
    {
        if (_fundChosenByUser || !ShowFunds || !IsNew) return; // never changes an existing entry by itself
        var fund = CategoryOptions.ElementAtOrDefault(value)?.Value is { } category && _fundByCategory.TryGetValue(category, out var id) ? id : (int?)null;
        SetFund(fund);
    }

    private void SetFund(int? fundId)
    {
        _settingFund = true;
        FundIndex = Math.Max(0, FundOptions.ToList().FindIndex(o => o.Value == fundId));
        _settingFund = false;
    }

    private async Task LoadFundsAsync(int? current)
    {
        FundOptions.Clear();
        FundOptions.Add(new("Not from a fund", null));
        var funds = await goals.GetFundsAsync();
        foreach (var f in funds)
        {
            FundOptions.Add(new($"{f.Goal.Name}  ({Money.Format(f.CurrentCents)} set aside)", f.Goal.Id));
        }
        if (current is { } id && FundOptions.All(o => o.Value != id))
        {
            FundOptions.Add(new("A fund that's archived", id));
        }
        _fundByCategory = funds.Where(f => f.Goal.CategoryId is not null).GroupBy(f => f.Goal.CategoryId!.Value).ToDictionary(g => g.Key, g => g.First().Goal.Id);
        _fundChosenByUser = current is not null;
        SetFund(current);
        OnPropertyChanged(nameof(ShowFunds));
    }

    /// <summary>"Usually $11.99 from Checking (4 times)", once a known payee is chosen.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHabitHint))] public partial string? HabitHint { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUseAmount))] public partial string? UseAmountText { get; set; }
    public bool HasUseAmount => UseAmountText is not null;
    public bool HasHabitHint => HabitHint is not null;
    private long? _typicalCents;
    private int _loadedAccountIndex;
    private bool _reconciledChangeConfirmed;

    /// <summary>0 = Spending, 1 = Income, 2 = Transfer, 3 = Refund.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransfer), nameof(IsNotTransfer), nameof(AccountLabel), nameof(CategoryLabel), nameof(PayeeLabel), nameof(PayeePlaceholder), nameof(IsRefund), nameof(IsOutside), nameof(ShowFunds))]
    public partial int TypeIndex { get; set; }

    [ObservableProperty] public partial string AmountText { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? Date { get; set; }
    [ObservableProperty] public partial string Payee { get; set; } = "";

    // A different payee means the usual-amount hint no longer applies (it's worked out again on leaving the box).
    partial void OnPayeeChanged(string value)
    {
        HabitHint = null;
        UseAmountText = null;
        _typicalCents = null;
    }
    [ObservableProperty] public partial int AccountIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOutside), nameof(AccountLabel), nameof(ToAccountLabel))]
    public partial int ToAccountIndex { get; set; }

    /// <summary>For a transfer with someone else's account: 0 = money out to them, 1 = money in from them.</summary>
    [ObservableProperty] public partial int OutsideDirectionIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial string Memo { get; set; } = "";
    [ObservableProperty] public partial bool IsPending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    /// <summary>Set once anything has been saved, so the list knows to refresh.</summary>
    public bool SavedAny { get; private set; }

    public bool HasError => ErrorMessage is not null;
    public bool IsNew => _editingId is null;
    public string Title => IsNew ? "Add transaction" : "Edit transaction";
    private TransactionType SelectedType => TypeOrder[Math.Clamp(TypeIndex, 0, TypeOrder.Length - 1)];
    public bool IsTransfer => SelectedType == TransactionType.Transfer;
    public bool IsNotTransfer => !IsTransfer;
    public bool IsOutside => IsTransfer && ToAccountOptions.ElementAtOrDefault(ToAccountIndex)?.Value == OutsideAccount;
    public string AccountLabel => IsOutside ? "Your account" : IsTransfer ? "From account" : "Account";
    public string ToAccountLabel => IsOutside ? "Other side" : "To account";

    /// <summary>The kind choice for a stored type: transfers with someone else's account show as Transfer.</summary>
    private static int KindIndexOf(TransactionType type) =>
        Array.IndexOf(TypeOrder, type is TransactionType.OutsideIn or TransactionType.OutsideOut ? TransactionType.Transfer : type);
    public string CategoryLabel => IsTransfer ? "Savings category (optional)" : "Category";
    public bool IsRefund => SelectedType == TransactionType.Refund;
    public string PayeeLabel => SelectedType switch
    {
        TransactionType.Income => "From (payer)",
        TransactionType.Refund => "Refunded by (store)",
        TransactionType.Transfer => "Description (optional)",
        _ => "Payee",
    };
    public string PayeePlaceholder => SelectedType switch
    {
        TransactionType.Income => "e.g. Riverside Market",
        TransactionType.Refund => "e.g. Target",
        TransactionType.Transfer => "e.g. Card payment",
        _ => "e.g. Main Street Grocery",
    };

    public async Task LoadForCreateAsync(int? accountId = null)
    {
        _editingId = null;
        _original = null;
        await LoadListsAsync();
        await LoadFundsAsync(null);
        TypeIndex = 0;
        RefreshCategories();

        var lastAccount = int.TryParse(await settings.GetAsync(LastAccountKey), CultureInfo.InvariantCulture, out var last) ? last : (int?)null;
        AccountIndex = IndexOfAccount(accountId ?? lastAccount);
        ToAccountIndex = 0;
        ResetEntryFields();
        Date = time.GetLocalNow().Date;
        IsPending = false;
    }

    /// <summary>A new entry filled in from a what-if (Make it real): nothing is saved until you save.</summary>
    public async Task LoadForPlannedAsync(string? name, long amountCents, DateOnly date, int accountId, bool isIncome)
    {
        await LoadForCreateAsync(accountId);
        AllowAddAnother = false;
        TypeIndex = Array.IndexOf(TypeOrder, isIncome ? TransactionType.Income : TransactionType.Expense);
        AmountText = Money.FormatForEditing(amountCents);
        Payee = name ?? "";
        Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
        Hint = "Filled in from your what-if. Check it, then save to make it real.";
    }

    /// <summary>
    /// Prepares a new income entry for a paycheck: the estimate is only a starting point for
    /// the user to replace with the amount actually paid.
    /// </summary>
    public async Task LoadForPaycheckAsync(string payer, long estimatedNetCents, DateOnly payDate, int? accountId, int? categoryId, bool fromPaystub = false)
    {
        await LoadForCreateAsync(accountId);
        AllowAddAnother = false;
        TypeIndex = Array.IndexOf(TypeOrder, TransactionType.Income);
        AmountText = Money.FormatForEditing(estimatedNetCents);
        Payee = payer;
        Date = new DateTimeOffset(payDate.ToDateTime(TimeOnly.MinValue));
        var paycheckCategory = categoryId ?? _tree
            .SelectMany(n => n.Children.Prepend(n.Category))
            .FirstOrDefault(c => c.CategoryType == CategoryType.Income && c.Name == "Paycheck" && !c.IsArchived)?.Id;
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == paycheckCategory));
        Hint = fromPaystub
            ? "Filled in from your paystub. Check it matches the amount actually deposited."
            : "This is Ledger's estimate. Change it to the amount actually deposited.";
    }

    /// <summary>
    /// Prepares a new transaction for one occurrence of a recurring rule, filled in from the rule.
    /// The date is the scheduled date, or today when recording ahead of it.
    /// </summary>
    public async Task LoadForRecurringAsync(RecurringTransaction rule, DateOnly scheduledDate)
    {
        await LoadForCreateAsync(rule.AccountId);
        AllowAddAnother = false;
        TypeIndex = Array.IndexOf(TypeOrder, rule.TransactionType);
        AmountText = Money.FormatForEditing(rule.AmountCents);
        Payee = rule.Name;
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        var date = scheduledDate < today ? scheduledDate : today;
        Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
        ToAccountIndex = Math.Max(0, ToAccountOptions.ToList().FindIndex(o => o.Value == rule.TransferAccountId));
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == rule.CategoryId));
        Memo = rule.Memo ?? "";
        Hint = rule.IsAmountEstimated
            ? "The amount is an estimate. Change it to what was actually paid."
            : "Check the amount and date match what actually happened.";
    }

    /// <summary>The transaction created by the last successful save, if it was a new one.</summary>
    public int? CreatedTransactionId { get; private set; }

    /// <summary>False when "Save and add another" makes no sense (editing, or recording a paycheck).</summary>
    public bool AllowAddAnother { get; private set; } = true;

    /// <summary>Optional guidance shown at the top of the dialog.</summary>
    public string? Hint { get; private set; }
    public bool HasHint => Hint is not null;

    public async Task LoadForEditAsync(int transactionId)
    {
        var t = await transactions.GetAsync(transactionId);
        _editingId = t.Id;
        _original = t;
        await LoadListsAsync();

        TypeIndex = KindIndexOf(t.TransactionType);
        RefreshCategories();
        AmountText = Money.FormatForEditing(t.AmountCents);
        Date = new DateTimeOffset(t.Date.ToDateTime(TimeOnly.MinValue));
        Payee = t.Payee ?? "";
        AccountIndex = IndexOfAccount(t.AccountId);
        ToAccountIndex = t.TransactionType is TransactionType.OutsideIn or TransactionType.OutsideOut
            ? ToAccountOptions.Count - 1
            : Math.Max(0, ToAccountOptions.ToList().FindIndex(o => o.Value == t.TransferAccountId));
        OutsideDirectionIndex = t.TransactionType == TransactionType.OutsideIn ? 1 : 0;
        await LoadFundsAsync(t.PaidFromGoalId);
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == t.CategoryId));
        Memo = t.Memo ?? "";
        IsPending = t.Status == TransactionStatus.Pending;
        ErrorMessage = null;
    }

    /// <summary>Loads accounts and categories. Archived ones are offered only if this transaction already uses them.</summary>
    private async Task LoadListsAsync()
    {
        _accounts = (await accounts.GetAccountBalancesAsync(includeArchived: true)).Select(b => b.Account).ToList();
        _tree = await categories.GetTreeAsync(includeArchived: true);

        var usable = _accounts.Where(a => !a.IsArchived || a.Id == _original?.AccountId || a.Id == _original?.TransferAccountId).ToList();
        AccountOptions.Clear();
        ToAccountOptions.Clear();
        ToAccountOptions.Add(new("Choose an account", null));
        foreach (var a in usable)
        {
            AccountOptions.Add(new(a.Name, a.Id));
            ToAccountOptions.Add(new(a.Name, a.Id));
        }
        ToAccountOptions.Add(new("Someone else's account (outside)", OutsideAccount));
    }

    partial void OnTypeIndexChanged(int value)
    {
        var previous = CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value;
        RefreshCategories();
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == previous));
    }

    private void RefreshCategories()
    {
        var kind = SelectedType switch
        {
            TransactionType.Income => CategoryType.Income,
            TransactionType.Transfer => CategoryType.Savings,
            _ => CategoryType.Expense,
        };
        CategoryOptions.Clear();
        CategoryOptions.Add(new("No category", null));
        foreach (var node in _tree.Where(n => n.Category.CategoryType == kind))
        {
            if (Usable(node.Category))
            {
                CategoryOptions.Add(new(node.Category.Name, node.Category.Id));
            }
            foreach (var child in node.Children.Where(Usable))
            {
                CategoryOptions.Add(new($"{node.Category.Name} › {child.Name}", child.Id));
            }
        }

        bool Usable(Category c) => !c.IsArchived || c.Id == _original?.CategoryId;
    }

    /// <summary>Refreshes the payee suggestions as the user types.</summary>
    public async Task UpdatePayeeSuggestionsAsync(string text)
    {
        try
        {
            var found = await transactions.GetPayeeSuggestionsAsync(text, SelectedType);
            PayeeSuggestions.Clear();
            foreach (var s in found.Where(s => !string.Equals(s.Payee, text.Trim(), StringComparison.Ordinal)))
            {
                PayeeSuggestions.Add(s);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Payee suggestions failed");
        }
    }

    /// <summary>Uses a past payee, then what you usually do with it.</summary>
    public async Task ChoosePayeeAsync(PayeeSuggestion suggestion)
    {
        Payee = suggestion.Payee;
        if (CategoryIndex == 0 && suggestion.CategoryId is { } id)
        {
            CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == id));
        }
        await ApplyHabitsAsync();
    }

    /// <summary>
    /// For a payee used before: fills the usual category if none is chosen, and for a new entry the
    /// usual account if you haven't picked one; shows the usual amount with a button to use it.
    /// Everything it changes is visible in the form, and nothing is saved until you save.
    /// </summary>
    public async Task ApplyHabitsAsync()
    {
        HabitHint = null;
        UseAmountText = null;
        _typicalCents = null;
        if (IsTransfer || string.IsNullOrWhiteSpace(Payee)) return;
        try
        {
            if (await suggestions.GetHabitsAsync(Payee, SelectedType) is not { } habits) return;
            if (CategoryIndex == 0 && habits.CategoryId is { } category)
            {
                CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == category));
            }
            string? accountName = null;
            if (habits.AccountId is { } account && AccountOptions.ToList().FindIndex(o => o.Value == account) is var index and >= 0)
            {
                if (IsNew && AccountIndex == _loadedAccountIndex) AccountIndex = index;
                accountName = AccountOptions[index].Label;
            }
            var amount = habits.AmountVaries
                ? $"{Money.Format(habits.LowCents)}–{Money.Format(habits.HighCents)}"
                : Money.Format(habits.TypicalCents);
            HabitHint = $"Usually {amount}" + (accountName is null ? "" : $" from {accountName}") + $" ({habits.Entries} recent entries)";
            if (string.IsNullOrWhiteSpace(AmountText))
            {
                _typicalCents = habits.TypicalCents;
                UseAmountText = $"Use {Money.Format(habits.TypicalCents)}";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Payee habits failed");
        }
    }

    public void UseTypicalAmount()
    {
        if (_typicalCents is { } cents) AmountText = Money.FormatForEditing(cents);
        UseAmountText = null;
    }

    public async Task UpdateNoteSuggestionsAsync(string text)
    {
        try
        {
            var found = await suggestions.GetNoteSuggestionsAsync(text);
            NoteSuggestions.Clear();
            foreach (var note in found) NoteSuggestions.Add(note);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Note suggestions failed");
        }
    }

    /// <summary>Validates and saves. Returns false and sets <see cref="ErrorMessage"/> on failure.</summary>
    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (!Money.TryParse(AmountText, out var cents) || cents <= 0)
        {
            ErrorMessage = "Enter an amount greater than zero, like 8.42.";
            return false;
        }
        if (Date is not { } date)
        {
            ErrorMessage = "Choose a date.";
            return false;
        }
        if (AccountOptions.ElementAtOrDefault(AccountIndex)?.Value is not { } accountId)
        {
            ErrorMessage = "Choose an account.";
            return false;
        }

        var type = IsOutside ? (OutsideDirectionIndex == 1 ? TransactionType.OutsideIn : TransactionType.OutsideOut) : SelectedType;
        var reconciled = _original?.Status == TransactionStatus.Reconciled;
        var input = new TransactionInput(
            type,
            cents,
            DateOnly.FromDateTime(date.Date),
            accountId,
            IsTransfer && !IsOutside ? ToAccountOptions.ElementAtOrDefault(ToAccountIndex)?.Value : null,
            CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value,
            Payee,
            Memo,
            // A reconciled transaction stays reconciled when saved (a payee or category fix changes nothing on the statement).
            IsPending ? TransactionStatus.Pending : reconciled ? TransactionStatus.Reconciled : TransactionStatus.Cleared,
            ShowFunds ? FundOptions.ElementAtOrDefault(FundIndex)?.Value : null);

        // Changing what a statement shows (amount, date, kind or account) of a reconciled one: say so, and save on the second try.
        if (reconciled && !_reconciledChangeConfirmed && _original is { } o
            && (o.AmountCents != input.AmountCents || o.Date != input.Date || o.TransactionType != input.Type
                || o.AccountId != input.AccountId || o.TransferAccountId != input.TransferAccountId))
        {
            _reconciledChangeConfirmed = true;
            ErrorMessage = "This transaction is reconciled with a bank statement. Changing its amount, date, kind or account means that statement won't match any more. Press Save again to change it anyway.";
            return false;
        }

        IsSaving = true;
        try
        {
            if (_editingId is { } id)
            {
                await transactions.UpdateAsync(id, input);
            }
            else
            {
                CreatedTransactionId = (await transactions.CreateAsync(input)).Id;
                await settings.SetAsync(LastAccountKey, accountId.ToString(CultureInfo.InvariantCulture));
            }
            SavedAny = true;
            return true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save transaction");
            ErrorMessage = "Something went wrong while saving this transaction. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>After "Save and add another": keeps type, account and date; clears the rest.</summary>
    public void PrepareNext() => ResetEntryFields();

    private void ResetEntryFields()
    {
        AmountText = "";
        Payee = "";
        Memo = "";
        CategoryIndex = 0;
        ErrorMessage = null;
        PayeeSuggestions.Clear();
        NoteSuggestions.Clear();
        HabitHint = null;
        UseAmountText = null;
        _typicalCents = null;
        _loadedAccountIndex = AccountIndex;
        _fundChosenByUser = false;
        if (FundOptions.Count > 0) SetFund(null);
    }

    private int IndexOfAccount(int? id) => Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == id));
}
