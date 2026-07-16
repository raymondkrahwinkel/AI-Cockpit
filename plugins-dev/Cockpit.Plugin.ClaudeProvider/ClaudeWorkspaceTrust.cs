using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Marks a working directory trusted in the <c>.claude.json</c> the CLI reads for a spawn, so the TUI does not
/// block on its interactive trust dialog on first render — a copy of the host's <c>WorkspaceTrustWriter</c>
/// (weg A). Read-merge-write: preserves every other key and project entry, creates what is absent, idempotent.
/// </summary>
internal static class ClaudeWorkspaceTrust
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static void MarkWorkingDirectoryTrusted(string configDir, string absoluteWorkingDirectory)
    {
        var claudeJsonPath = Path.Combine(configDir, ".claude.json");
        var root = ReadOrCreateRoot(claudeJsonPath);

        if (root["projects"] is not JsonObject projects)
        {
            projects = [];
            root["projects"] = projects;
        }

        if (projects[absoluteWorkingDirectory] is not JsonObject projectEntry)
        {
            projectEntry = [];
            projects[absoluteWorkingDirectory] = projectEntry;
        }

        projectEntry["hasTrustDialogAccepted"] = true;

        Directory.CreateDirectory(configDir);
        using var stream = File.Create(claudeJsonPath);
        JsonSerializer.Serialize(stream, root, SerializerOptions);
    }

    private static JsonObject ReadOrCreateRoot(string claudeJsonPath)
    {
        if (!File.Exists(claudeJsonPath))
        {
            return [];
        }

        using var stream = File.OpenRead(claudeJsonPath);
        var node = JsonNode.Parse(stream);
        return node as JsonObject ?? [];
    }
}
