using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class RecurringEditorDialog : ContentDialog
{
    public RecurringEditorViewModel ViewModel { get; }

    public RecurringEditorDialog(RecurringEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus(FocusState.Programmatic);
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
