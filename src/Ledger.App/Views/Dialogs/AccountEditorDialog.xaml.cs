using Ledger.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class AccountEditorDialog : ContentDialog
{
    public AccountEditorViewModel ViewModel { get; }

    public AccountEditorDialog(AccountEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => Form.FocusName();
    }

    public bool IsNotSaving(bool isSaving) => !isSaving;

    /// <summary>Keeps the dialog open, showing the problem, when saving fails.</summary>
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
