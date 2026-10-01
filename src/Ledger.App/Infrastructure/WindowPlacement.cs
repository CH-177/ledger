using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Ledger.App.Infrastructure;

/// <summary>
/// Remembers the window's position, size and maximized state between launches. Kept in a small
/// file beside the database rather than in it: it describes this PC's screen, so it shouldn't travel
/// with backups or exports.
/// </summary>
internal static class WindowPlacement
{
    private sealed record Saved(int X, int Y, int Width, int Height, bool Maximized);

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "window.json");

    /// <summary>Restores the last placement if it still fits on a connected display. Returns false otherwise.</summary>
    public static bool TryRestore(AppWindow window, int minWidth, int minHeight)
    {
        try
        {
            if (!File.Exists(FilePath) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is not { } s)
            {
                return false;
            }
            var rect = new RectInt32(s.X, s.Y, Math.Max(s.Width, minWidth), Math.Max(s.Height, minHeight));

            // Only if the title bar area is on a screen that's still connected (monitors change).
            var display = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (display is null)
            {
                return false;
            }
            var work = display.WorkArea;
            if (rect.X + 100 > work.X + work.Width || rect.Y < work.Y - 10 || rect.Y + 40 > work.Y + work.Height)
            {
                return false;
            }

            window.MoveAndResize(rect);
            if (s.Maximized && window.Presenter is OverlappedPresenter p)
            {
                p.Maximize();
            }
            return true;
        }
        catch (Exception)
        {
            return false; // a damaged file just means the default placement
        }
    }

    /// <summary>Saves the current placement. The restored (not maximized) bounds are what's remembered.</summary>
    public static void Save(AppWindow window, RectInt32? restoredBounds)
    {
        try
        {
            var maximized = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                return; // keep the last useful placement
            }
            var r = maximized && restoredBounds is { } b ? b : new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(r.X, r.Y, r.Width, r.Height, maximized)));
        }
        catch (Exception)
        {
            // Not worth interrupting closing for.
        }
    }
}
