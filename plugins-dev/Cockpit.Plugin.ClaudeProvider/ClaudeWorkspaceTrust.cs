using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Marks a working directory trusted in the <c>.claude.json</c> the CLI reads for a spawn, so the TUI does not
/// block on its interactive trust dialog on first render — a copy of the host's <c>WorkspaceTrustWriter</c>
/// (weg A). Read-merge-write: preserves every other key and project entry, creates what is absent, idempotent.
/// <para>
/// <c>~/.claude.json</c> is a single file shared with any <c>claude</c> the cockpit already has running (a live
/// interactive TTY rewrites it continuously), so both ends of this read-merge-write are hardened against that
/// concurrency:
/// <list type="bullet">
/// <item>The write is <b>atomic</b> (temp file + rename), never a <c>File.Create</c> truncate-in-place. The old
/// truncate left a zero-length window in which a concurrent <c>claude</c> read a half-written config, backed it up
/// (<c>.claude.json.backup.*</c>) and reset to defaults — dropping the just-started session's
/// <c>hasTrustDialogAccepted</c>, and with it every <c>--mcp-config</c> server (MCP is trust-gated), silently.</item>
/// <item>The write is <b>skipped when the directory is already trusted</b> — the common case — so it does not race
/// the live TTY at all.</item>
/// <item>The read <b>never downgrades an existing file to an empty root</b>: an existing file that will not parse as
/// an object is a torn/locked read, retried and then thrown, rather than silently replaced with <c>{}</c> — writing
/// that back would wipe every project and trust entry, the exact data loss this type guards against.</item>
/// <item>The atomic replace is <b>retried</b>: a concurrent reader holding the file open makes the OS rename fail
/// with a sharing violation, and surviving a concurrently-active <c>claude</c> is the whole point.</item>
/// </list>
/// Cross-process merging is still best-effort: two cockpit spawns that read, add different entries and write can lose
/// one of the two entries (last writer wins) — acceptable, since each re-marks its own directory on its next launch.
/// </para>
/// </summary>
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

    /// <summary>
    /// The existing config as an object, or a fresh empty root only when the file genuinely does not exist. An
    /// existing file that cannot be read as an object is treated as a transient torn/locked read (the live claude
    /// writing it non-atomically), retried, and — if it never becomes readable — <b>thrown</b>. It is deliberately
    /// never downgraded to an empty root: writing that back over a real file would wipe every project and trust entry.
    /// </summary>
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

    /// <summary>
    /// Serialises <paramref name="root"/> to a sibling temp file and renames it over the target, so a concurrent
    /// reader sees either the whole old file or the whole new one — never a zero-length middle state. The rename is
    /// retried past the sharing violation a concurrently-open reader can cause; the temp file is always cleaned up.
    /// </summary>
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
