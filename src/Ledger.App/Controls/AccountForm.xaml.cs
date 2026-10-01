using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Controls;

/// <summary>The account fields, shared by the account dialog and first-launch setup.</summary>
public sealed partial class AccountForm : UserControl
{
    public AccountForm() => InitializeComponent();

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(AccountEditorViewModel), typeof(AccountForm), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowDetailsProperty =
        DependencyProperty.Register(nameof(ShowDetails), typeof(bool), typeof(AccountForm), new PropertyMetadata(true));

    public AccountEditorViewModel ViewModel
    {
        get => (AccountEditorViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Shows the less common fields (institution, net worth). Hidden during first launch.</summary>
    public bool ShowDetails
    {
        get => (bool)GetValue(ShowDetailsProperty);
        set => SetValue(ShowDetailsProperty, value);
    }

    public void FocusName() => NameBox.Focus(FocusState.Programmatic);
}
