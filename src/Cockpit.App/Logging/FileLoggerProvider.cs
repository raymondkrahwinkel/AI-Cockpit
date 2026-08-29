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

        // AC-46: owner-only log permissions protect credentials beside the state root; writes keep the created mode.
        // AC-1216: shared roots can truncate a live log; AC-1214 override isolates opted-in instances, not a lock.
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
        // AC-1147: pid distinguishes two instances of the same build writing to the same file.
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [pid {Environment.ProcessId}] [{level}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_writeGate)
        {
            _RollIfOverLimit();
            _AppendWithRetry(line + Environment.NewLine);
        }
    }

    // AC-1216: a logging call must never throw. A short retry absorbs a transient sharing violation from a
    // second process writing the same file; it runs inside _writeGate, so the ceiling stays low (5 attempts,
    // 2ms apart, ~10ms) — past it this swallows like _RollIfOverLimit, a dropped line beats a stalled thread.
    private void _AppendWithRetry(string line)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.AppendAllText(_path, line);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(2);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
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
