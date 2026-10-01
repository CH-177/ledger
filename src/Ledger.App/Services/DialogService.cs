using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Services;

/// <summary>
/// Shows content dialogs in the main window. Dialogs live in a popup layer that
/// does not inherit the app's chosen theme, so it is applied here.
/// </summary>
public sealed class DialogService
{
    private FrameworkElement? _root;

    public void Attach(FrameworkElement root) => _root = root;

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("DialogService has not been attached to a window.");
        }
        dialog.XamlRoot = _root.XamlRoot;
        dialog.RequestedTheme = _root.ActualTheme;
        dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        return await dialog.ShowAsync();
    }

    /// <summary>Asks a yes/no question. Returns true only when the primary action is chosen.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
