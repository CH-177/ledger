using Ledger.App.Infrastructure;
using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.Core.Data;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Ledger.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly ILogger<App> _logger;

    /// <summary>
    /// The composition root. Pages resolve their view models from here; nothing
    /// else should use it as a service locator.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        _logger = Services.GetRequiredService<ILogger<App>>();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Closed += OnWindowClosed;
        _window.Activate();

        // A second launch is redirected here (see Program.Main): bring this window forward.
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
            _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            Services.GetRequiredService<DatabaseInitializer>().Checkpoint();
        }
        catch (Exception ex)
        {
            // Data is still safe in the log; it is recovered at the next launch.
            _logger.LogWarning(ex, "Checkpoint on close failed");
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            b.AddDebug();
            b.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));
        });

        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<LedgerDbContext>(o => o.UseSqlite($"Data Source={AppPaths.DatabasePath}"));

        // Core services
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<CategoryService>();
        services.AddSingleton<TransactionService>();
        services.AddSingleton<BudgetService>();
        services.AddSingleton<IncomeService>();
        services.AddSingleton<IncomeForecastService>();
        services.AddSingleton<RecurringService>();
        services.AddSingleton<SuggestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ScenarioService>();
        services.AddSingleton<ForecastService>();
        services.AddSingleton<GoalService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<DeductionService>();
        services.AddSingleton<PaystubService>();
        services.AddSingleton<Ledger.Core.Import.ImportService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ExportService>();

        // App services
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<AutoRecordService>();
        services.AddSingleton<WelcomeTour>();
        services.AddSingleton(sp => new FilePickerService(() => sp.GetRequiredService<MainWindow>()));

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<OverviewViewModel>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<AccountEditorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<CategoriesViewModel>();
        services.AddTransient<CategoryEditorViewModel>();
        services.AddTransient<TransactionsViewModel>();
        services.AddTransient<TransactionEditorViewModel>();
        services.AddTransient<BudgetViewModel>();
        services.AddTransient<IncomeViewModel>();
        services.AddTransient<JobEditorViewModel>();
        services.AddTransient<ShiftEditorViewModel>();
        services.AddTransient<PasteScheduleViewModel>();
        services.AddTransient<BillsViewModel>();
        services.AddTransient<RecurringEditorViewModel>();
        services.AddTransient<GoalsViewModel>();
        services.AddTransient<GoalEditorViewModel>();
        services.AddTransient<GoalMoneyViewModel>();
        services.AddTransient<GoalHistoryViewModel>();
        services.AddTransient<DataViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<PaystubViewModel>();
        services.AddTransient<ImportViewModel>();
        services.AddTransient<ReconcileViewModel>();
        services.AddTransient<PlanViewModel>();
        services.AddTransient<HelpViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Record the technical details locally; the exception message is not shown to the user.
        _logger.LogCritical(e.Exception, "Unhandled exception");
    }
}
