using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

// AC-1893: callers only enqueue; one writer owns the stream, rollover and retries.
public sealed class FileLoggerProvider : ILoggerProvider
{
    internal const long MaxSizeBytes = 8 * 1024 * 1024;
    internal const int QueueCapacity = 4096;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);
    private static readonly FileShare WriterShare = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;

    private readonly string _path;
    private readonly int _queueCapacity;
    private readonly Func<Stream> _openStream;
    private readonly object _queueGate = new();
    private readonly LinkedList<LogEntry> _queue = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Thread _writer;
    private bool _stopping;
    private bool _writing;
    private long _dropped;
    private long _droppedWarnings;
    private long _stopDeadline;
    private bool _prepared;

    public FileLoggerProvider(string path) : this(path, null)
    {
    }

    internal FileLoggerProvider(string path, Func<Stream>? openStream, int queueCapacity = QueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _path = path;
        _queueCapacity = queueCapacity;
        _openStream = openStream ?? (() => new FileStream(path, FileMode.Append, FileAccess.Write, WriterShare));
        _writer = new Thread(_WriteQueued) { IsBackground = true, Name = "cockpit-log-writer" };
        AppDomain.CurrentDomain.ProcessExit += _OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += _OnUnhandledException;
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose()
    {
        lock (_queueGate)
        {
            if (!_stopping)
            {
                _stopDeadline = Stopwatch.GetTimestamp() + (long)(DrainBudget.TotalSeconds * Stopwatch.Frequency);
            }

            _stopping = true;
            Monitor.PulseAll(_queueGate);
        }

        if (Thread.CurrentThread != _writer)
        {
            _writer.Join(DrainBudget);
        }

        AppDomain.CurrentDomain.ProcessExit -= _OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= _OnUnhandledException;
        _loggers.Clear();
    }

    // Called only on terminal paths; ordinary UI lifecycle messages remain nonblocking.
    internal void Flush()
    {
        var elapsed = Stopwatch.StartNew();
        lock (_queueGate)
        {
            while ((_queue.Count > 0 || _writing) && Thread.CurrentThread != _writer)
            {
                var remaining = DrainBudget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                Monitor.Wait(_queueGate, remaining);
            }
        }
    }

    private void _OnProcessExit(object? sender, EventArgs args) => Dispose();

    // FailFast and hard kills cannot run managed flush hooks; a wedged disk is bounded by DrainBudget.
    private void _OnUnhandledException(object sender, UnhandledExceptionEventArgs args) => Dispose();

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
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [pid {Environment.ProcessId}] [{level}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_queueGate)
        {
            if (_stopping)
            {
                return;
            }

            // Prefer the oldest low-priority line. An all-Warning+ backlog drops its oldest too:
            // bounded memory and nonblocking Log cannot guarantee lossless delivery to a stalled disk.
            if (_queue.Count == _queueCapacity && _queue.First is { } oldest)
            {
                var candidate = oldest;
                while (candidate.Value.Level >= LogLevel.Warning && candidate.Next is { } next)
                {
                    candidate = next;
                }

                var dropped = candidate.Value.Level < LogLevel.Warning ? candidate : oldest;
                _dropped++;
                _droppedWarnings += dropped.Value.Level >= LogLevel.Warning ? 1 : 0;
                _queue.Remove(dropped);
            }

            _queue.AddLast(new LogEntry(level, line + Environment.NewLine));
            Monitor.PulseAll(_queueGate);
        }

        Flush();
    }

    private void _WriteQueued()
    {
        Stream? stream = null;
        long length = 0;
        try
        {
            while (true)
            {
                LogEntry entry;
                long dropped;
                long warnings;
                lock (_queueGate)
                {
                    while (_queue.Count == 0 && !_stopping)
                    {
                        // Release exclusivity after a quiet burst so a restart can rotate and open the same log.
                        if (!Monitor.Wait(_queueGate, TimeSpan.FromMilliseconds(500)))
                        {
                            break;
                        }
                    }

                    if (_queue.Count == 0 && !_stopping)
                    {
                        entry = default;
                        dropped = 0;
                        warnings = 0;
                    }
                    else if (_queue.First is { } first)
                    {
                        entry = first.Value;
                        _queue.RemoveFirst();
                        _writing = true;
                        dropped = _dropped;
                        warnings = _droppedWarnings;
                        _dropped = 0;
                        _droppedWarnings = 0;
                    }
                    else
                    {
                        return;
                    }
                }

                if (entry.Line is null)
                {
                    _Close(ref stream);
                    continue;
                }

                if (dropped > 0)
                {
                    if (!_AppendWithRetry($"{dropped} log lines dropped (queue full; {warnings} at Warning or above){Environment.NewLine}", ref stream, ref length))
                    {
                        return;
                    }
                }

                if (!_AppendWithRetry(entry.Line, ref stream, ref length))
                {
                    return;
                }
                lock (_queueGate)
                {
                    _writing = false;
                    Monitor.PulseAll(_queueGate);
                }
            }
        }
        catch (Exception)
        {
            // This is the last logging sink: a broken writer must never crash the host.
        }
        finally
        {
            _Close(ref stream);

            lock (_queueGate)
            {
                _stopping = true;
                _writing = false;
                _queue.Clear();
                Monitor.PulseAll(_queueGate);
            }
        }
    }

    private bool _AppendWithRetry(string line, ref Stream? stream, ref long length)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        while (true)
        {
            try
            {
                if (stream is null)
                {
                    if (!_prepared)
                    {
                        if (File.Exists(_path))
                        {
                            // Test exclusivity before rotating generations; a handoff may still own the live file.
                            using var previousWriter = new FileStream(_path, FileMode.Open, FileAccess.Write, WriterShare);
                        }

                        CredentialFileHousekeeping.PrepareLogFile(_path);
                        _prepared = true;
                    }

                    stream = _openStream();
                    length = stream.Length;
                }

                if (length >= MaxSizeBytes)
                {
                    stream.Dispose();
                    stream = null;
                    try
                    {
                        File.Move(_path, RolloverPathFor(_path), overwrite: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // A tail reader may deny rename while permitting append; keep the crash tail writable.
                    }

                    stream = _openStream();
                    length = stream.Length;
                }

                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                length += bytes.Length;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _Close(ref stream);
            }

            lock (_queueGate)
            {
                if (_stopping && Stopwatch.GetTimestamp() >= _stopDeadline)
                {
                    return false;
                }
            }

            Thread.Sleep(10);
        }
    }

    private static void _Close(ref Stream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception)
        {
        }
        finally
        {
            stream = null;
        }
    }

    private readonly record struct LogEntry(LogLevel Level, string Line);

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
