using Ledger.App.Services;
using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class ReconcilePage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public ReconcileViewModel ViewModel { get; } = App.Services.GetRequiredService<ReconcileViewModel>();

    public ReconcilePage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync(e.Parameter as int?);

    private void OnBackClick(object sender, RoutedEventArgs e) => ViewModel.GoToAccounts();
    private void OnTickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAll(true);
    private void OnUntickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAll(false);
    private async void OnFinishClick(object sender, RoutedEventArgs e) => await ViewModel.FinishAsync();

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (await _dialogs.ConfirmAsync("Undo the latest reconciliation?",
                "Its transactions go back to not reconciled. Nothing else changes, and you can reconcile that statement again.",
                "Undo"))
        {
            await ViewModel.UndoLastAsync();
        }
    }
}
