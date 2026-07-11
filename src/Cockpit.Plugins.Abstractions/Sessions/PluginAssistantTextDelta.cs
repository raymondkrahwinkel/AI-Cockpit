namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>An incremental chunk of assistant text produced while streaming a turn.</summary>
public sealed record PluginAssistantTextDelta : PluginSessionEvent
{
    public required int BlockIndex { get; init; }

    public required string Text { get; init; }
}
