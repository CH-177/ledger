using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.Services;

/// <summary>
/// Records due bills that are set to record automatically, at startup and whenever the Bills page
/// opens (so a day change while Ledger is open is caught too), and remembers what it did so the
/// Bills page can tell the user until they dismiss it.
/// </summary>
public sealed class AutoRecordService(RecurringService recurring, ILogger<AutoRecordService> logger)
{
    private readonly List<AutoRecordedItem> _recorded = [];
    private readonly List<AutoRecordFailure> _failed = [];

    public IReadOnlyList<AutoRecordedItem> Recorded => _recorded;
    public IReadOnlyList<AutoRecordFailure> Failed => _failed;

    public async Task RunAsync()
    {
        try
        {
            var result = await recurring.AutoRecordDueAsync();
            _recorded.AddRange(result.Recorded);
            // A failure is reported once per date; it stays in "Needs recording" on the Bills page.
            foreach (var f in result.Failed.Where(f => !_failed.Any(x => x.Name == f.Name && x.Date == f.Date)))
            {
                _failed.Add(f);
            }
            if (!result.IsEmpty)
            {
                logger.LogInformation("Auto-recorded {Recorded} bill(s); {Failed} could not be recorded", result.Recorded.Count, result.Failed.Count);
            }
        }
        catch (Exception ex)
        {
            // Never block the app: anything not recorded simply shows under "Needs recording".
            logger.LogError(ex, "Automatic recording failed");
        }
    }

    public void Dismiss()
    {
        _recorded.Clear();
        _failed.Clear();
    }
}
