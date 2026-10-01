using Ledger.App.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ledger.App.Services;

/// <summary>The top-level destinations in the navigation rail.</summary>
public enum AppPage
{
    Overview,
    Transactions,
    Budget,
    Income,
    Bills,
    Goals,
    Accounts,
    Reports,

    /// <summary>What if? Scenarios and "Can I afford this?".</summary>
    Plan,
    Settings,

    /// <summary>Opened from Settings; the rail keeps Settings selected.</summary>
    Categories,

    /// <summary>Bank-file import, opened from Transactions; the rail keeps Transactions selected.</summary>
    Import,

    /// <summary>Statement reconciliation, opened from Accounts; the rail keeps Accounts selected.</summary>
    Reconcile,

    /// <summary>Help topics; F1 opens the topic for the current page.</summary>
    Help,
}

/// <summary>Lets view models move between pages without referencing views.</summary>
public sealed class NavigationService
{
    private static readonly Dictionary<AppPage, Type> Pages = new()
    {
        [AppPage.Overview] = typeof(OverviewPage),
        [AppPage.Transactions] = typeof(TransactionsPage),
        [AppPage.Budget] = typeof(BudgetPage),
        [AppPage.Income] = typeof(IncomePage),
        [AppPage.Bills] = typeof(BillsPage),
        [AppPage.Goals] = typeof(GoalsPage),
        [AppPage.Accounts] = typeof(AccountsPage),
        [AppPage.Reports] = typeof(ReportsPage),
        [AppPage.Settings] = typeof(SettingsPage),
        [AppPage.Categories] = typeof(CategoriesPage),
        [AppPage.Import] = typeof(ImportPage),
        [AppPage.Reconcile] = typeof(ReconcilePage),
        [AppPage.Plan] = typeof(PlanPage),
        [AppPage.Help] = typeof(HelpPage),
    };

    private Frame? _frame;

    public AppPage? Current { get; private set; }

    /// <summary>Raised after a page change so the navigation rail can reflect it.</summary>
    public event EventHandler<AppPage>? Navigated;

    public void Attach(Frame frame) => _frame = frame;

    /// <summary>
    /// Shows <paramref name="page"/>. A <paramref name="parameter"/> (e.g. an account id to
    /// filter by) is passed to the page and forces navigation even if it is already showing.
    /// </summary>
    public void Navigate(AppPage page, object? parameter = null)
    {
        if (_frame is null || (Current == page && parameter is null))
        {
            return;
        }
        _frame.Navigate(Pages[page], parameter, new EntranceNavigationTransitionInfo());
        Current = page;
        Navigated?.Invoke(this, page);
    }
}
