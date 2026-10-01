using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class GoalMoneyDialog : ContentDialog
{
    public GoalMoneyViewModel ViewModel { get; }

    public GoalMoneyDialog(GoalMoneyViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => AmountBox.Focus(FocusState.Programmatic);
    }

    public bool IsNotSaving(bool isSaving) => !isSaving;

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.SaveAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
