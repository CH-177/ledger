using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Controls;

/// <summary>A friendly placeholder shown when a page has nothing to display yet.</summary>
public sealed partial class EmptyState : UserControl
{
    public EmptyState() => InitializeComponent();

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState),
            new PropertyMetadata("", (d, _) => ((EmptyState)d).UpdateActionVisibility()));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(EmptyState),
            new PropertyMetadata(null, (d, _) => ((EmptyState)d).UpdateActionVisibility()));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>The action button only appears when there is both a label and something for it to do.</summary>
    private void UpdateActionVisibility() =>
        ActionButton.Visibility = !string.IsNullOrEmpty(ActionText) && Command is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
}
