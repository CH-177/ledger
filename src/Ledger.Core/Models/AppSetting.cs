namespace Ledger.Core.Models;

/// <summary>A user preference stored alongside the financial data so backups carry it.</summary>
public class AppSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
