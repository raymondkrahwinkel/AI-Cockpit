using Microsoft.Extensions.Logging;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Hands every category the same capturing logger, so a test can read what a class it does not construct itself
/// wrote — the proxy builds its forwarder internally, and the forwarder's log lines are the only evidence that a
/// failure on the streaming path was handled rather than escaping.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _entriesLock = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entriesLock)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    /// <summary>Waits for a line that matches, because the relay writes it from a request that has already returned.</summary>
    public async Task<bool> WaitForAsync(Func<(LogLevel Level, string Message), bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Entries.Any(match))
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return Entries.Any(match);
    }

    private sealed class Sink(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._entriesLock)
            {
                owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class Scope : IDisposable
        {
            public static readonly Scope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
