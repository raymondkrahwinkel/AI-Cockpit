using Microsoft.Extensions.Logging;

namespace Cockpit.Core.Tests;

/// <summary>
/// Captures log entries so a test can assert a failure was actually logged, not swallowed silently.
/// </summary>
/// <remarks>
/// Its own file rather than a private class inside one test: "it was logged" is the contract in more than one
/// place, because more than one thing in this cockpit is started fire-and-forget and can only report by logging.
/// A second copy of this would be a second thing to keep in step.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
