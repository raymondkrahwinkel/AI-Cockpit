using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.ClaudeProvider;

// Marks a working directory trusted in `.claude.json` so the TUI skips its interactive trust dialog on spawn
// — a copy of the host's `WorkspaceTrustWriter`. Shared with any already-running `claude`, so writes are
// atomic (temp file + rename) and reads never downgrade an unparseable file to empty, to avoid data loss.
internal static class ClaudeWorkspaceTrust
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    // A concurrently-writing claude only leaves the shared file unreadable for a few milliseconds; a handful of quick
    // retries ride out that window for both the read and the atomic replace before giving up.
    private const int MaxAttempts = 5;

    public static void MarkWorkingDirectoryTrusted(string configDir, string absoluteWorkingDirectory)
    {
        var claudeJsonPath = Path.Combine(configDir, ".claude.json");
        var root = ReadRootOrThrow(claudeJsonPath);

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

        // Already trusted: do not rewrite the shared file at all. Every needless rewrite races a live TTY claude that
        // is also writing ~/.claude.json, and skipping it keeps that race from ever stripping this session's servers.
        if (projectEntry["hasTrustDialogAccepted"] is JsonValue existing
            && existing.TryGetValue<bool>(out var trusted) && trusted)
        {
            return;
        }

        projectEntry["hasTrustDialogAccepted"] = true;

        Directory.CreateDirectory(configDir);
        WriteAtomically(claudeJsonPath, root);
    }

    // An existing file that can't be read as an object is treated as a transient torn/locked read, retried,
    // and thrown if it never recovers — never downgraded to an empty root, which would wipe every project
    // and trust entry on write-back.
    private static JsonObject ReadRootOrThrow(string claudeJsonPath)
    {
        if (!File.Exists(claudeJsonPath))
        {
            return [];
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = File.OpenRead(claudeJsonPath);
                if (JsonNode.Parse(stream) is JsonObject root)
                {
                    return root;
                }

                // Parsed, but not an object (a bare array/null, or a torn write that happens to be valid JSON) —
                // fall through to the retry/throw rather than accept it as an empty root.
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A locked handle or an incomplete document mid-write — retry below.
            }

            if (attempt >= MaxAttempts)
            {
                throw new IOException(
                    $"'{claudeJsonPath}' exists but could not be read as a JSON object after {MaxAttempts} attempts; " +
                    "refusing to overwrite it with an empty config.");
            }

            Thread.Sleep(20 * attempt);
        }
    }

    // Serialises `root` to a sibling temp file and renames it over the target, so a concurrent
    // reader sees either the whole old file or the whole new one — never a zero-length middle state. The rename is
    // retried past the sharing violation a concurrently-open reader can cause; the temp file is always cleaned up.
    private static void WriteAtomically(string claudeJsonPath, JsonObject root)
    {
        var tempPath = claudeJsonPath + ".cockpit-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, root, SerializerOptions);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, claudeJsonPath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A concurrent reader holding the target open (without share-delete) makes the OS replace fail;
                    // give it a few short windows before surfacing the failure to the caller.
                    if (attempt >= MaxAttempts)
                    {
                        throw;
                    }

                    Thread.Sleep(20 * attempt);
                }
            }
        }
        catch
        {
            // The launch fails on a persistent write error rather than proceeding: a headless SDK spawn with an
            // unmarked directory blocks on a trust dialog it can never answer, so failing fast beats hanging. Clean up
            // the temp file on the way out.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // A leftover temp file is not worth compounding the failure over.
            }

            throw;
        }
    }
}
