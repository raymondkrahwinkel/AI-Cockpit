namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Something went wrong in the plugin's driver itself (request failure, parse failure, ...).</summary>
public sealed record PluginSessionError : PluginSessionEvent
{
    public required string Message { get; init; }
}
