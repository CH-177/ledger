using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class CategoryEditorDialog : ContentDialog
{
    public CategoryEditorViewModel ViewModel { get; }

    public CategoryEditorDialog(CategoryEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        };
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
