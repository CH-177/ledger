using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class CategoriesPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();
    private readonly NavigationService _navigation = App.Services.GetRequiredService<NavigationService>();

    public CategoriesViewModel ViewModel { get; } = App.Services.GetRequiredService<CategoriesViewModel>();

    public CategoriesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private void OnBackClick(object sender, RoutedEventArgs e) => _navigation.Navigate(AppPage.Settings);

    private async void OnAddClick(object sender, RoutedEventArgs e) => await AddAsync(groupId: null);

    private async Task AddAsync(int? groupId)
    {
        var editor = App.Services.GetRequiredService<CategoryEditorViewModel>();
        await editor.LoadForCreateAsync(groupId);
        await ShowEditorAsync(editor);
    }

    private async Task RenameAsync(CategoryRowViewModel row)
    {
        var editor = App.Services.GetRequiredService<CategoryEditorViewModel>();
        editor.LoadForRename(row);
        await ShowEditorAsync(editor);
    }

    private async Task ShowEditorAsync(CategoryEditorViewModel editor)
    {
        if (await _dialogs.ShowAsync(new CategoryEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    /// <summary>Builds the row's menu on demand, since move destinations depend on the current tree.</summary>
    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CategoryRowViewModel row } anchor)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(Item("Rename", "", async () => await RenameAsync(row)));

        if (row.IsGroup)
        {
            menu.Items.Add(Item("Add subcategory", "", async () => await AddAsync(row.Id)));
        }

        var targets = ViewModel.GetMoveTargets(row);
        if (targets.Count > 0)
        {
            var move = new MenuFlyoutSubItem { Text = row.IsGroup ? "Move into group" : "Move to", Icon = new FontIcon { Glyph = "" } };
            foreach (var target in targets)
            {
                move.Items.Add(Item(target.Label, null, async () => await ViewModel.MoveAsync(row, target)));
            }
            menu.Items.Add(move);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Archive", "", async () => await ArchiveAsync(row)));
        menu.ShowAt(anchor);
    }

    private async Task ArchiveAsync(CategoryRowViewModel row)
    {
        var message = row.IsGroup && row.Children.Count > 0
            ? $"Its {row.Children.Count} subcategories will be archived too. Past transactions keep their categories, and you can restore them at any time."
            : "It won't be offered for new transactions. Past transactions keep it, and you can restore it at any time.";
        if (await _dialogs.ConfirmAsync($"Archive {row.FullName}?", message, "Archive"))
        {
            await ViewModel.SetArchivedAsync(row, archived: true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CategoryRowViewModel row)
        {
            await ViewModel.SetArchivedAsync(row, archived: false);
        }
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }
        item.Click += async (_, _) => await action();
        return item;
    }
}
