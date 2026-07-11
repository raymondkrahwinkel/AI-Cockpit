namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>A turn finished.</summary>
public sealed record PluginTurnCompleted : PluginSessionEvent
{
    public required string Subtype { get; init; }

    public required string? Result { get; init; }

    public required bool IsError { get; init; }

    public string? StopReason { get; init; }
}
