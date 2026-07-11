namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>The result of a previously requested tool call.</summary>
public sealed record PluginToolResult : PluginSessionEvent
{
    public required string ToolUseId { get; init; }

    public required string Content { get; init; }

    public required bool IsError { get; init; }
}
