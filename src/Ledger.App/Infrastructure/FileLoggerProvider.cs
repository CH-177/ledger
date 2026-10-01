using System.Text;
using Microsoft.Extensions.Logging;

namespace Ledger.App.Infrastructure;

/// <summary>
/// Writes technical diagnostics to a daily file under the local data folder and
/// keeps the last few days. Callers must never log amounts, payees or other
/// financial content; log identifiers and event names instead.
/// </summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private const int DaysToKeep = 7;
    private readonly Lock _gate = new();
    private bool _pruned;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                if (!_pruned)
                {
                    Prune();
                    _pruned = true;
                }
                var file = Path.Combine(directory, $"ledger-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Diagnostics must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-DaysToKeep);
        foreach (var file in Directory.EnumerateFiles(directory, "ledger-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }
            provider.Write(line);
        }
    }
}
