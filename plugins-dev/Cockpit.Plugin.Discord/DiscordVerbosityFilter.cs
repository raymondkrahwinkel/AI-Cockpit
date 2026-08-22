using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord;

/// <summary>
/// Decides, per <see cref="AssistantChannelVerbosity"/> (AC-669 §1.4), whether a transcript row is relayed to
/// Discord at all and what text it is relayed as. Pure and Discord-agnostic, so it is testable without a socket.
/// </summary>
internal static class DiscordVerbosityFilter
{
    /// <summary>
    /// Whether <paramref name="kind"/> is relayed at all under <paramref name="verbosity"/>.
    /// </summary>
    public static bool ShouldRelay(AssistantChannelRowKind kind, AssistantChannelVerbosity verbosity) => verbosity switch
    {
        // A — the finished answer only. An error is part of that answer, not tool traffic, so it still shows.
        AssistantChannelVerbosity.FinalAnswerOnly =>
            kind is AssistantChannelRowKind.AssistantText or AssistantChannelRowKind.TurnCompleted or AssistantChannelRowKind.Error,
        // B — everything, tool use included. C relays the same set, just rendered shorter (see Render).
        AssistantChannelVerbosity.Everything or AssistantChannelVerbosity.StatusLines => kind is not AssistantChannelRowKind.Divider,
        _ => false,
    };

    /// <summary>
    /// What to send for a row that already passed <see cref="ShouldRelay"/>.
    /// </summary>
    public static string Render(AssistantChannelRow row, AssistantChannelVerbosity verbosity)
    {
        if (verbosity != AssistantChannelVerbosity.StatusLines)
        {
            return row.Text;
        }

        return row.Kind switch
        {
            AssistantChannelRowKind.ToolUse => $"\U0001F527 {row.ToolName ?? "a tool"}…",
            AssistantChannelRowKind.ToolResult => $"✓ {row.ToolName ?? "tool"} done",
            AssistantChannelRowKind.Thinking => "\U0001F4AD thinking…",
            _ => row.Text,
        };
    }
}
