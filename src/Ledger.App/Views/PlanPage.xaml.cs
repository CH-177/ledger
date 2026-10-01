using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views.Dialogs;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class PlanPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public PlanViewModel ViewModel { get; } = App.Services.GetRequiredService<PlanViewModel>();

    public PlanPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private async void OnCheckAffordClick(object sender, RoutedEventArgs e) => await ViewModel.CheckAffordAsync();
    private async void OnAddChangeClick(object sender, RoutedEventArgs e) => await ViewModel.AddChangeAsync();
    private async void OnSaveClick(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    private async void OnClearClick(object sender, RoutedEventArgs e) => await ViewModel.ClearAsync();

    private async void OnRemoveChangeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScenarioChangeRowViewModel row) await ViewModel.RemoveChangeAsync(row);
    }

    private async void OnDeleteSavedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SavedIndex >= 0 && await _dialogs.ConfirmAsync("Delete this saved scenario?", "Only the saved what-if goes. Nothing real changes.", "Delete"))
        {
            await ViewModel.DeleteSavedAsync();
        }
    }

    /// <summary>
    /// "Make it real" opens the usual editor filled in from the assumption. Only saving there creates
    /// anything; the scenario itself never does.
    /// </summary>
    private async void OnMakeRealClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScenarioChangeRowViewModel row) return;
        var c = row.Change;
        switch (c.Kind)
        {
            case ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome when c.AccountId is { } account:
                var editor = App.Services.GetRequiredService<TransactionEditorViewModel>();
                await editor.LoadForPlannedAsync(c.Name, c.AmountCents, c.From, account, c.Kind == ScenarioChangeKind.ExtraIncome);
                await _dialogs.ShowAsync(new TransactionEditorDialog(editor));
                break;
            case ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome when c.AccountId is { } account:
                var bill = App.Services.GetRequiredService<RecurringEditorViewModel>();
                var type = c.Kind == ScenarioChangeKind.AddBill ? TransactionType.Expense : TransactionType.Income;
                await bill.LoadFromRepeatAsync(new RepeatSuggestion("", c.Name ?? "", type, account, null, c.Frequency, c.AmountCents, false, 0, c.From, c.From, c.From));
                await _dialogs.ShowAsync(new RecurringEditorDialog(bill));
                break;
        }
    }
}
