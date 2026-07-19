using System.Collections.Concurrent;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

/// <summary>
/// A minimal append-to-file <see cref="ILoggerProvider"/> so the app has a readable log when it runs
/// detached (double-clicked / Start-Process) — where there is no console to capture. Writes are
/// serialized behind a lock; the file is truncated at startup so each run starts clean. Deliberately
/// tiny (no rolling/retention): a single-user desktop tool's diagnostic trail, not a logging framework.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string path)
    {
        _path = path;

        // Owner-only, dir and file (AC-46): the log lives under the state root beside the credential files, and a
        // stock umask would otherwise leave it world-readable. This truncates for a clean run; Write only appends
        // afterwards, so the restricted mode set here carries for the life of the file.
        CredentialFileHousekeeping.PrepareLogFile(path);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose() => _loggers.Clear();

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_writeGate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
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
