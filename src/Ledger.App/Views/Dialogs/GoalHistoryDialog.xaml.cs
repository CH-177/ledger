using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class GoalHistoryDialog : ContentDialog
{
    public GoalHistoryViewModel ViewModel { get; }

    public GoalHistoryDialog(GoalHistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GoalContributionRowViewModel row)
        {
            await ViewModel.RemoveAsync(row);
        }
    }
}
