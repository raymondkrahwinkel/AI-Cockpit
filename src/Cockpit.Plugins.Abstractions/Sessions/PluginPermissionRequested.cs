namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>The driver is asking the host to allow or deny a tool call.</summary>
public sealed record PluginPermissionRequested : PluginSessionEvent
{
    public required string ToolUseId { get; init; }

    public required string ToolName { get; init; }

    public required string InputJson { get; init; }
}
