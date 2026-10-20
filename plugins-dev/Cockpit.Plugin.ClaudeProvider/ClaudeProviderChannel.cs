using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider;

// What the backend part answers the config view over the plugin's channel: the view may run on another machine
// than the profile, so every file-system question goes to the backend. Linked into both assemblies as source.
internal static class ClaudeProviderChannel
{
    // Payload ignored. Answers a ClaudeConfigChoicesAnswer.
    public const string ConfigChoices = "config-choices";

    // Payload: ClaudeCheckConfigRequest. Answers a ClaudeCheckConfigAnswer.
    public const string CheckConfig = "check-config";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record ClaudeConfigDirChoice(string Label, string ConfigDir);

internal sealed record ClaudeConfigChoicesAnswer(IReadOnlyList<ClaudeConfigDirChoice> ConfigDirs, IReadOnlyList<string> Executables);

internal sealed record ClaudeCheckConfigRequest(string? ConfigDir, string? ExecutablePath);

internal sealed record ClaudeCheckConfigAnswer(bool ConfigDirOk, string? ConfigDirText, bool ExecutableOk, string ExecutableText);
