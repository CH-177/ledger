using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Form state for creating or editing one account.</summary>
public sealed partial class AccountEditorViewModel(AccountService accounts, TimeProvider time, ILogger<AccountEditorViewModel> logger)
    : ObservableObject
{
    private int? _editingId;

    public IReadOnlyList<AccountTypeOption> Types => AccountTypeDisplay.All;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartingBalanceLabel), nameof(StartingBalanceHelp))]
    public partial int SelectedTypeIndex { get; set; }

    [ObservableProperty]
    public partial string InstitutionName { get; set; } = "";

    [ObservableProperty]
    public partial string StartingBalanceText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsIncludedInNetWorth { get; set; } = true;

    /// <summary>The day the starting balance is from. Empty means before every transaction (accounts from before 2.4.2).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? BalanceDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public bool HasError => ErrorMessage is not null;

    public bool IsNew => _editingId is null;

    public string Title => IsNew ? "Add account" : "Edit account";

    private AccountType SelectedType =>
        AccountTypeDisplay.All[Math.Clamp(SelectedTypeIndex, 0, AccountTypeDisplay.All.Count - 1)].Type;

    public string StartingBalanceLabel =>
        SelectedType.IsLiability() ? "Amount owed when you started tracking" : "Starting balance";

    public string StartingBalanceHelp => SelectedType.IsLiability()
        ? "What you owed on this card, as your bank showed it on the day below."
        : "What was in this account, as your bank showed it on the day below.";

    public string BalanceDateHelp =>
        "Transactions on or before this day are already in that balance, so importing or entering them later won't count them twice.";

    public void LoadNew(string name = "", AccountType type = AccountType.Checking)
    {
        _editingId = null;
        Name = name;
        SelectedTypeIndex = AccountTypeDisplay.IndexOf(type);
        InstitutionName = "";
        StartingBalanceText = "";
        IsIncludedInNetWorth = true;
        BalanceDate = time.GetLocalNow().Date;
        ErrorMessage = null;
    }

    public void LoadExisting(Account account)
    {
        _editingId = account.Id;
        Name = account.Name;
        SelectedTypeIndex = AccountTypeDisplay.IndexOf(account.AccountType);
        InstitutionName = account.InstitutionName ?? "";
        StartingBalanceText = Money.FormatForEditing(account.AccountType.ToEnteredBalance(account.StartingBalanceCents));
        IsIncludedInNetWorth = account.IsIncludedInNetWorth;
        BalanceDate = account.BalanceDate is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
        ErrorMessage = null;
    }

    /// <summary>Validates and saves. Returns false and sets <see cref="ErrorMessage"/> on failure.</summary>
    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Give the account a name.";
            return false;
        }

        long entered = 0;
        if (!string.IsNullOrWhiteSpace(StartingBalanceText) && !Money.TryParse(StartingBalanceText, out entered))
        {
            ErrorMessage = "Enter the amount as a number, like 250.00.";
            return false;
        }

        var type = SelectedType;
        var input = new AccountInput(Name, type, type.ToStoredBalance(entered), InstitutionName, IsIncludedInNetWorth,
            BalanceDate is { } date ? DateOnly.FromDateTime(date.Date) : null);

        IsSaving = true;
        try
        {
            if (_editingId is { } id)
            {
                await accounts.UpdateAsync(id, input);
            }
            else
            {
                await accounts.CreateAsync(input);
            }
            return true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save account");
            ErrorMessage = "Something went wrong while saving this account. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
