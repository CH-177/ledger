using Ledger.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class PaystubDialog : ContentDialog
{
    public PaystubViewModel ViewModel { get; }

    public PaystubDialog(PaystubViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
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
