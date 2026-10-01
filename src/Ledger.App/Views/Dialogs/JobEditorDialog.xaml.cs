using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class JobEditorDialog : ContentDialog
{
    public JobEditorViewModel ViewModel { get; }

    public JobEditorDialog(JobEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    public bool IsNotSaving(bool isSaving) => !isSaving;

    private void OnRemoveDeductionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeductionRowViewModel row)
        {
            ViewModel.RemoveDeduction(row);
        }
    }

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
