using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Core.Models;

namespace Ledger.Core.Payroll;

/// <summary>One row of an IRS annual percentage-method table (Pub 15-T), in cents.</summary>
public sealed record TaxBracket(long Over, long Base, decimal Rate);

/// <summary>
/// Payroll tax reference data for one year, loaded from <c>Data/Reference/tax-YYYY.json</c>.
/// </summary>
public sealed class TaxTables
{
    public int Year { get; init; }
    public required FederalTables Federal { get; init; }
    public required SocialSecurityTables SocialSecurity { get; init; }
    public required MedicareTables Medicare { get; init; }
    public required PennsylvaniaTables Pennsylvania { get; init; }

    public sealed class FederalTables
    {
        public required Dictionary<FilingStatus, long> Step1gCents { get; init; }
        public required Dictionary<FilingStatus, TaxBracket[]> Standard { get; init; }
        public required Dictionary<FilingStatus, TaxBracket[]> Step2 { get; init; }
    }

    public sealed record SocialSecurityTables(decimal Rate, long WageBaseCents);
    public sealed record MedicareTables(decimal Rate, decimal AdditionalRate, long AdditionalThresholdCents);
    public sealed record PennsylvaniaTables(decimal IncomeTaxRate, decimal UnemploymentEmployeeRate, long LstAnnualCapCents, long LstLumpSumMaxCents);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<IReadOnlyDictionary<int, TaxTables>> All = new(LoadAll);

    /// <summary>Years with tables available.</summary>
    public static IReadOnlyCollection<int> AvailableYears => All.Value.Keys.ToList();

    /// <summary>
    /// Tables for <paramref name="year"/>, or the latest earlier year when none exist yet.
    /// <paramref name="isExact"/> tells callers to label estimates that use a stand-in year.
    /// </summary>
    public static TaxTables For(int year, out bool isExact)
    {
        if (All.Value.TryGetValue(year, out var exact))
        {
            isExact = true;
            return exact;
        }
        isExact = false;
        var fallback = All.Value.Keys.Where(y => y < year).DefaultIfEmpty(All.Value.Keys.Min()).Max();
        return All.Value[fallback];
    }

    private static IReadOnlyDictionary<int, TaxTables> LoadAll()
    {
        var assembly = typeof(TaxTables).Assembly;
        var result = new Dictionary<int, TaxTables>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Ledger.Reference.tax-", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var tables = JsonSerializer.Deserialize<TaxTables>(stream, Options)
                ?? throw new InvalidOperationException($"Could not read {name}.");
            result[tables.Year] = tables;
        }
        if (result.Count == 0)
        {
            throw new InvalidOperationException("No tax reference data is embedded.");
        }
        return result;
    }
}
