namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Something went wrong in the plugin's driver itself (request failure, parse failure, ...).
/// </summary>
public sealed record PluginSessionError : PluginSessionEvent
{
    public required string Message { get; init; }

    // AC-720: optional, defaults to Unknown — a plugin that never sets it still compiles and renders
    // in the host's informational (grey) presentation rather than a guessed severity.
    public PluginSessionErrorKind Kind { get; init; } = PluginSessionErrorKind.Unknown;

    // When the driver knows the earliest a retry might succeed (e.g. an HTTP Retry-After header).
    public DateTimeOffset? RetryAfter { get; init; }
}
