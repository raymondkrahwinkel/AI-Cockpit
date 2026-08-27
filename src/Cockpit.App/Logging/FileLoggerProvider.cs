using System.Collections.Concurrent;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

// Minimal locked file logging for detached launches that have no console. Each startup truncates the log, while
// rollover bounds growth during long runs (AC-741), following `UsageHistoryLog` (AC-399).
public sealed class FileLoggerProvider : ILoggerProvider
{
    // The live file rolls to `.1` once it reaches this size. Named constant so the trade-off (disk
    // footprint vs. how far back the log reaches) is visible at the call site, matching UsageHistoryLog.MaxSizeBytes.
    internal const long MaxSizeBytes = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly string _rolloverPath;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        _rolloverPath = RolloverPathFor(path);

        // Owner-only, dir and file (AC-46): the log lives under the state root beside the credential files, and a
        // stock umask would otherwise leave it world-readable. This truncates for a clean run; Write only appends
        // afterwards, so the restricted mode set here carries for the life of the file.
        CredentialFileHousekeeping.PrepareLogFile(path);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose() => _loggers.Clear();

    // `cockpit.log` rolled to `cockpit.log.1` — derived from the live path (not hardcoded) so a
    // test pointed at an arbitrary file gets a rollover file next to it, matching UsageHistoryLog.RolloverPathFor.
    internal static string RolloverPathFor(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        var stem = Path.GetFileNameWithoutExtension(logFilePath);
        var extension = Path.GetExtension(logFilePath);
        var rolloverName = $"{stem}.1{extension}";
        return string.IsNullOrEmpty(directory) ? rolloverName : Path.Combine(directory, rolloverName);
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_writeGate)
        {
            _RollIfOverLimit();
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    // Overwrites whatever rollover file already exists — one generation of history kept beyond the
    // live file, not an unbounded chain, same call taken in UsageHistoryLog. Swallows failures (no
    // logger to report to here — this *is* the log): worst case the file keeps growing past the limit.
    private void _RollIfOverLimit()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxSizeBytes)
            {
                return;
            }

            File.Move(_path, _rolloverPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
