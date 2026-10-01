using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>Well-known preference keys.</summary>
public static class SettingKeys
{
    public const string Theme = "Appearance.Theme";

    /// <summary>The colour scheme's name (see <see cref="Services.ColorScheme"/>); missing or unknown means "Ledger".</summary>
    public const string ColorScheme = "Appearance.Scheme";

    /// <summary>"false" hides the decorative art beside pages on wide windows; anything else shows it.</summary>
    public const string SideArt = "Appearance.SideArt";

    /// <summary>"0" turns off the daily automatic backup; anything else (or nothing) leaves it on.</summary>
    public const string AutomaticBackups = "Backup.Automatic";

    /// <summary>"false" turns off spotting repeating entries to suggest as bills; anything else leaves it on.</summary>
    public const string SpotRepeats = "Suggestions.SpotRepeats";

    /// <summary>Repeats the user said not to suggest again, one key per line (see RepeatSuggestion.Key).</summary>
    public const string DismissedRepeats = "Suggestions.DismissedRepeats";

    /// <summary>Saved what-if scenarios (their assumptions only), as JSON.</summary>
    public const string Scenarios = "Planning.Scenarios";
}

/// <summary>
/// Stores user preferences in the database so they travel with backups.
/// </summary>
public sealed class SettingsService(IDbContextFactory<LedgerDbContext> contextFactory)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var existing = await db.Settings.FindAsync([key], ct);
        if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}
