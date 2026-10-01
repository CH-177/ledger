namespace Ledger.App.Infrastructure;

/// <summary>Where Ledger keeps its files. Everything stays on this PC.</summary>
/// <remarks>
/// Debug builds use a separate "Ledger-Dev" folder so development and testing can
/// never read or modify the real data used by the installed app. Setting
/// LEDGER_DATA_DIR overrides the location entirely (used for isolated test runs).
/// The portable copy (a "Ledger.portable" file beside Ledger.exe) keeps its data in a
/// "Data" folder next to it instead, so the whole thing can live on a USB stick.
/// </remarks>
public static class AppPaths
{
#if DEBUG
    private const string FolderName = "Ledger-Dev";
#else
    private const string FolderName = "Ledger";
#endif

    public const string PortableMarker = "Ledger.portable";

    /// <summary>True for the portable copy, which keeps its data beside it.</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string DataDirectory { get; } = EnsureDirectory(
        Environment.GetEnvironmentVariable("LEDGER_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : IsPortable
                ? Path.Combine(AppContext.BaseDirectory, "Data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName));

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "ledger.db");

    public static string LogsDirectory { get; } = EnsureDirectory(Path.Combine(DataDirectory, "Logs"));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
