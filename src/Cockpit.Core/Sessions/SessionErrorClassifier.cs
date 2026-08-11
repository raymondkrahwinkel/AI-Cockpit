namespace Cockpit.Core.Sessions;

// AC-720: text-only fallback for a driver that has not been taught to set Kind itself (still Unknown).
// Presentation-only — picks a row's color/icon, never behavior such as an automatic retry.
public static class SessionErrorClassifier
{
    public static SessionErrorKind Classify(string message)
    {
        if (_ContainsAny(message, _AuthSignals))
        {
            return SessionErrorKind.AuthRequired;
        }

        if (_ContainsAny(message, _RateLimitSignals))
        {
            return SessionErrorKind.RateLimited;
        }

        return _ContainsAny(message, _ServiceUnavailableSignals)
            ? SessionErrorKind.ServiceUnavailable
            : SessionErrorKind.Unknown;
    }

    private static bool _ContainsAny(string message, string[] signals) =>
        signals.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] _AuthSignals =
    [
        "unauthorized",
        "not logged in",
        "not authenticated",
        "invalid api key",
        "api key",
        "authentication",
    ];

    private static readonly string[] _RateLimitSignals =
    [
        "rate limit",
        "rate_limit",
        "too many requests",
        "quota",
    ];

    private static readonly string[] _ServiceUnavailableSignals =
    [
        "timed out",
        "timeout",
        "connection refused",
        "overloaded",
        "service unavailable",
        "bad gateway",
    ];
}
