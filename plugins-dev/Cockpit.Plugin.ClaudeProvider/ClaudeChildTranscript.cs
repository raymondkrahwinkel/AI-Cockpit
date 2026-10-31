using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Shared;

namespace Cockpit.Plugin.ClaudeProvider;

internal static class ClaudeChildTranscript
{
    public static PluginChildTranscriptPage Read(string stateDirectory, string workingDirectory, string owner, string agent, string? cursor, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(owner, "^[a-zA-Z0-9-]+$") || !Regex.IsMatch(agent, "^[a-zA-Z0-9-]+$"))
        {
            return PluginChildTranscriptPage.Unavailable("invalid source identity");
        }
        try
        {
            string project = Regex.Replace(Path.GetFullPath(workingDirectory), "[^a-zA-Z0-9]", "-");
            string path = Path.Combine(stateDirectory, "projects", project, owner, "subagents", $"agent-{agent}.jsonl");
            return TtyChildSnapshot.SafePath(path, stateDirectory)
                ? JsonlChildTranscript.Read(path, agent, owner, cursor, cancellationToken)
                : PluginChildTranscriptPage.Unavailable("invalid or linked transcript source");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PluginChildTranscriptPage.Unavailable("transcript is missing or inaccessible");
        }
    }
}
