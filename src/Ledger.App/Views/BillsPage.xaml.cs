using CommunityToolkit.Mvvm.Input;
using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views.Dialogs;
using Ledger.Core;
using Ledger.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class BillsPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();
    private readonly NavigationService _navigation = App.Services.GetRequiredService<NavigationService>();

    public BillsViewModel ViewModel { get; } = App.Services.GetRequiredService<BillsViewModel>();

    public IAsyncRelayCommand AddCommand { get; }
    public IRelayCommand GoToAccountsCommand { get; }

    public BillsPage()
    {
        AddCommand = new AsyncRelayCommand(() => EditAsync(null));
        GoToAccountsCommand = new RelayCommand(() => _navigation.Navigate(AppPage.Accounts));
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    // ---- Rules --------------------------------------------------------------------------------

    private async Task EditAsync(int? ruleId)
    {
        var editor = App.Services.GetRequiredService<RecurringEditorViewModel>();
        try
        {
            if (ruleId is { } id)
            {
                await editor.LoadForEditAsync(id);
            }
            else
            {
                await editor.LoadForCreateAsync();
            }
        }
        catch (LedgerValidationException)
        {
            await ViewModel.LoadAsync();
            return;
        }
        if (await _dialogs.ShowAsync(new RecurringEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnMakeBillClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RepeatRowViewModel row) return;
        var editor = App.Services.GetRequiredService<RecurringEditorViewModel>();
        await editor.LoadFromRepeatAsync(row.Suggestion);
        if (await _dialogs.ShowAsync(new RecurringEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnDismissRepeatClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RepeatRowViewModel row) await ViewModel.DismissRepeatAsync(row);
    }

    private async void OnEditRuleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleRowViewModel row)
        {
            await EditAsync(row.Rule.Id);
        }
    }

    private async void OnPauseRuleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleRowViewModel row)
        {
            await ViewModel.TogglePausedAsync(row);
        }
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleRowViewModel row
            && await _dialogs.ConfirmAsync($"Delete {row.Name}?", "It will leave the forecast. Transactions you already recorded are not affected.", "Delete"))
        {
            await ViewModel.DeleteAsync(row);
        }
    }

    // ---- Occurrences --------------------------------------------------------------------------

    private async void OnRecordDueClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DueRowViewModel row)
        {
            await RecordAsync(row.Occurrence.Rule, row.Occurrence.Date);
        }
    }

    private async void OnSkipDueClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DueRowViewModel row)
        {
            await ViewModel.SkipAsync(row.Occurrence.Rule.Id, row.Occurrence.Date);
        }
    }

    private async void OnSkipAllClick(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.Due.Count;
        if (await _dialogs.ConfirmAsync($"Skip all {count} missed payment{(count == 1 ? "" : "s")}?",
                "Use this when they were handled outside Ledger or didn't happen. Nothing is recorded, and each one leaves the forecast.",
                "Skip all"))
        {
            await ViewModel.SkipAllDueAsync();
        }
    }

    private async void OnRecordForecastClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ForecastRowViewModel row
            && ViewModel.RuleFor(row.Event.RecurringId) is { } rule && row.Event.ScheduledDate is { } date)
        {
            await RecordAsync(rule, date);
        }
    }

    private async void OnSkipForecastClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ForecastRowViewModel row
            && row.Event.RecurringId is { } id && row.Event.ScheduledDate is { } date)
        {
            await ViewModel.SkipAsync(id, date);
        }
    }

    private void OnOpenIncomeClick(object sender, RoutedEventArgs e) => _navigation.Navigate(AppPage.Income);

    /// <summary>
    /// Opens the transaction dialog filled in from the rule. Only once the user saves is the new
    /// transaction linked to that date. Nothing is recorded automatically.
    /// </summary>
    private async Task RecordAsync(RecurringTransaction rule, DateOnly date)
    {
        var editor = App.Services.GetRequiredService<TransactionEditorViewModel>();
        await editor.LoadForRecurringAsync(rule, date);
        await _dialogs.ShowAsync(new TransactionEditorDialog(editor));
        if (editor.CreatedTransactionId is { } transactionId)
        {
            await ViewModel.RecordAsync(rule.Id, date, transactionId);
        }
    }
}
