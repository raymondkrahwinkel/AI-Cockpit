using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// Before spawning a session, marks a working directory as trusted in the <c>.claude.json</c> the CLI
/// will actually read for that spawn so it does not show its interactive trust dialog in a headless
/// stream-json process (which has no way to answer it). The caller passes the effective config directory
/// (see <c>ClaudeConfigDirectory.ResolveConfigJsonDirectory</c>) — the profile dir for a non-default
/// profile, the home root for a default-dir profile whose CLAUDE_CONFIG_DIR stays unset.
/// </summary>
/// <remarks>
/// Synchronous by design: this needs to complete before <see cref="ClaudeCliProcess.Start"/>
/// spawns the process, and that seam is itself synchronous — sync file I/O here avoids a
/// sync-over-async call at the one call site instead.
/// </remarks>
internal sealed class WorkspaceTrustWriter : ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Sets <c>projects["&lt;absoluteWorkingDirectory&gt;"].hasTrustDialogAccepted = true</c> in
    /// <paramref name="configDir"/>'s <c>.claude.json</c>. Read-merge-write: preserves every
    /// other key already in the file (and every other project entry), creates the file/the
    /// project entry if absent, and is idempotent — re-running it on an already-trusted
    /// directory is a no-op write of the same content.
    /// </summary>
    public void MarkWorkingDirectoryTrusted(string configDir, string absoluteWorkingDirectory)
    {
        var claudeJsonPath = Path.Combine(configDir, ".claude.json");
        var root = ReadOrCreateRoot(claudeJsonPath);

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
        }

        if (projects[absoluteWorkingDirectory] is not JsonObject projectEntry)
        {
            projectEntry = new JsonObject();
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
