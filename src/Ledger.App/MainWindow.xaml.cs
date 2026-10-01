using System.Runtime.InteropServices;
using Ledger.App.Help;
using Ledger.App.Infrastructure;
using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace Ledger.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1240;
    private const int DefaultHeight = 820;
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 560;

    private readonly NavigationService _navigation;
    private readonly ThemeService _themes;
    private readonly WelcomeTour _tour;

    public ShellViewModel ViewModel { get; }

    public MainWindow(ShellViewModel viewModel, NavigationService navigation, ThemeService themes, DialogService dialogs, WelcomeTour tour)
    {
        ViewModel = viewModel;
        _navigation = navigation;
        _themes = themes;
        _tour = tour;
        InitializeComponent();

        ConfigureWindow();
        _navigation.Attach(ContentFrame);
        dialogs.Attach(RootGrid);
        _navigation.Navigated += OnNavigated;
        _themes.ThemeChanged += (_, _) => UpdateCaptionButtonColors();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        RootGrid.Loaded += async (_, _) => await StartAsync();
        _tour.Changed += OnTourChanged;
        _themes.SideArtChanged += (_, _) => UpdateSideArt();

        // F1 anywhere: Help, opened at the topic for the page on screen.
        var help = new KeyboardAccelerator { Key = VirtualKey.F1 };
        help.Invoked += (_, args) =>
        {
            if (ViewModel.IsReady)
            {
                _navigation.Navigate(AppPage.Help, HelpContent.TopicFor(_navigation.Current));
                args.Handled = true;
            }
        };
        RootGrid.KeyboardAccelerators.Add(help);
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Ledger.ico"));

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }

        // Reopen where it was last time; the default size above is the fallback.
        WindowPlacement.TryRestore(AppWindow, (int)(MinimumWidth * scale), (int)(MinimumHeight * scale));
        TrackRestoredBounds();
        Closed += (_, _) => WindowPlacement.Save(AppWindow, _restoredBounds);

        UpdateCaptionButtonColors();
    }

    private RectInt32? _restoredBounds;

    /// <summary>Remembers the normal (not maximized) bounds, so a maximized window reopens with a sensible restore size.</summary>
    private void TrackRestoredBounds()
    {
        void Capture()
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                _restoredBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            }
        }
        Capture();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange || e.DidSizeChange || e.DidPresenterChange)
            {
                Capture();
            }
        };
    }

    /// <summary>Restores and focuses the window when another launch is redirected to it.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private async Task StartAsync()
    {
        if (!await ViewModel.InitializeAsync())
        {
            return;
        }
        await _themes.InitializeAsync(RootGrid);

        if (ViewModel.IsOnboarding)
        {
            var onboarding = App.Services.GetRequiredService<OnboardingViewModel>();
            onboarding.Completed += (_, _) =>
            {
                ViewModel.IsOnboarding = false;
                OnboardingHost.Content = null;
                _navigation.Navigate(AppPage.Overview);
                if (onboarding.TakeTour)
                {
                    _tour.Start(skipWelcome: true);
                }
                else
                {
                    _ = _tour.MarkSeenAsync();
                }
            };
            OnboardingHost.Content = new OnboardingView(onboarding);
            return;
        }

        _navigation.Navigate(AppPage.Overview);

        // Offered once: on the first launch of a version with the tour, for people who set up before it.
        if (!await _tour.HasBeenSeenAsync())
        {
            _tour.Start();
        }
    }

    // ---- Side art -------------------------------------------------------------------------------

    /// <summary>Narrower than this, the strip beside the page column is left empty.</summary>
    private const double MinimumArtWidth = 160;

    private void OnContentAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSideArt();

    /// <summary>
    /// Fills the space to the right of the page column (which stops at LedgerContentMaxWidth) with
    /// the art, when there's enough of it and the art is on. Off in high contrast, where it would
    /// only be noise.
    /// </summary>
    private void UpdateSideArt()
    {
        var column = (double)Application.Current.Resources["LedgerContentMaxWidth"];
        var room = ContentArea.ActualWidth - column - 16; // keep clear of the page's scrollbar edge
        var show = _themes.ShowSideArt && room >= MinimumArtWidth
                   && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        SideArt.Width = Math.Max(0, room);
        SideArt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Welcome tour ---------------------------------------------------------------------------

    private void OnTourChanged(object? sender, EventArgs e)
    {
        if (!_tour.IsRunning)
        {
            TourTip.IsOpen = false;
            return;
        }
        var step = _tour.Current;
        TourTip.Title = step.Title;
        TourText.Text = step.Text;
        TourProgress.Text = _tour.ProgressText;
        TourBack.Visibility = _tour.IsFirst ? Visibility.Collapsed : Visibility.Visible;
        TourTip.ActionButtonContent = _tour.IsFirst ? "Start the tour" : _tour.IsLast ? "Done" : "Next";
        TourTip.CloseButtonContent = _tour.IsFirst ? "Not now" : _tour.IsLast ? null : "End tour";
        TourTip.IconSource = step.IsImportant ? new FontIconSource { Glyph = "\uE7BA" } : null;
        TourTip.Target = step.Page is { } page ? RailItemFor(page) : null;
        TourTip.PreferredPlacement = TourTip.Target is null ? TeachingTipPlacementMode.Center : TeachingTipPlacementMode.Right;
        // The tip moves while open (closing and reopening loses the reopen to the close animation).
        // Opening waits for the new page to lay out, so it points at the right item.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_tour.IsRunning)
            {
                TourTip.IsOpen = true;
            }
        });
    }

    private FrameworkElement? RailItemFor(AppPage page) => page == AppPage.Settings
        ? NavView.SettingsItem as FrameworkElement
        : NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == page.ToString());

    private void OnTourActionClick(TeachingTip sender, object args) => _tour.Next();

    private void OnTourBackClick(object sender, RoutedEventArgs e) => _tour.Back();

    /// <summary>The close button (or Esc) ends the tour; closing it to move between steps doesn't.</summary>
    private void OnTourClosed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (args.Reason != TeachingTipCloseReason.Programmatic)
        {
            _tour.End();
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await StartAsync();

    /// <summary>
    /// Handles clicks on the rail. ItemInvoked (unlike SelectionChanged) also fires for the
    /// already-selected item, so clicking Settings from the Categories sub-page goes back.
    /// </summary>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _navigation.Navigate(AppPage.Settings);
        }
        else if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
        {
            _navigation.Navigate(page);
        }
    }

    /// <summary>Keeps the rail's selection in step with the current page, wherever navigation started.</summary>
    private void OnNavigated(object? sender, AppPage page)
    {
        NavView.SelectedItem = page is AppPage.Settings or AppPage.Categories
            ? NavView.SettingsItem
            : NavView.MenuItems.Concat(NavView.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => (string)i.Tag == page switch
                {
                    AppPage.Import => nameof(AppPage.Transactions),
                    AppPage.Reconcile => nameof(AppPage.Accounts),
                    _ => page.ToString(),
                });
    }

    /// <summary>
    /// The system caption buttons follow neither the app theme nor the colour scheme on their own,
    /// so they take the scheme's text colours for the current light/dark theme.
    /// </summary>
    private void UpdateCaptionButtonColors()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        var foreground = _themes.GetColor("LedgerTextPrimaryBrush", dark);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        // A faint wash of the text colour, like the rest of the app's hover states.
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb((byte)(dark ? 24 : 18), foreground.R, foreground.G, foreground.B);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb((byte)(dark ? 36 : 28), foreground.R, foreground.G, foreground.B);
        titleBar.ButtonInactiveForegroundColor = _themes.GetColor("LedgerTextTertiaryBrush", dark);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
