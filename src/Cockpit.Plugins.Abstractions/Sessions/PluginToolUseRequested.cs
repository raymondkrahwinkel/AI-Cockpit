namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>The model requested a tool call.</summary>
public sealed record PluginToolUseRequested : PluginSessionEvent
{
    public required string ToolUseId { get; init; }

    public required string ToolName { get; init; }

    public required string InputJson { get; init; }
}
