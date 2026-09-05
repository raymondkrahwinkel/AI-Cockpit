using System.Text.Json;
using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Depot;

// A mirror's shadow state (AC-281): `.cockpit-sync/` next to the mirrored files, holding `base/` (bytes as of
// the last successful pull) and `index.json` (that file's Depot checksum plus its own size/mtime). Also the only
// place that writes into a mirror's tree — every write goes through the same atomic swap and path-safety check.
internal static class ShadowSyncStorage
{
    private static readonly JsonSerializerOptions _SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string SyncRoot(string mirrorPath) => Path.Combine(mirrorPath, ".cockpit-sync");

    public static string BaseRoot(string mirrorPath) => Path.Combine(SyncRoot(mirrorPath), "base");

    private static string _IndexFile(string mirrorPath) => Path.Combine(SyncRoot(mirrorPath), "index.json");

    // Null only when index.json exists but does not parse — a caller must treat that as a reason to stop, not as
    // "no files pulled yet": guessing empty here would forget every local divergence check and could overwrite
    // an operator's edit that this index was the only record of.
    public static IReadOnlyDictionary<string, ShadowIndexEntry>? LoadIndex(string mirrorPath)
    {
        var path = _IndexFile(mirrorPath);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ShadowIndexEntry>(StringComparer.Ordinal);
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ShadowIndexEntry>>(File.ReadAllText(path), _SerializerOptions);
            return (entries ?? []).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void SaveIndex(string mirrorPath, IReadOnlyDictionary<string, ShadowIndexEntry> entries) =>
        _WriteAtomically(_IndexFile(mirrorPath), JsonSerializer.Serialize(entries.Values.ToList(), _SerializerOptions));

    // Writes `content` into the mirror's working tree at `relativePath` and returns the size/mtime the file
    // landed with, for the index entry — atomically, so a crash mid-write never leaves a half file behind.
    public static (long Size, DateTimeOffset Mtime) WriteWorkingFile(string mirrorPath, string relativePath, string content)
    {
        var fullPath = ResolveSafePath(mirrorPath, relativePath);
        _WriteAtomically(fullPath, content);

        var info = new FileInfo(fullPath);
        return (info.Length, info.LastWriteTimeUtc);
    }

    public static void WriteBaseFile(string mirrorPath, string relativePath, string content) =>
        _WriteAtomically(ResolveSafePath(BaseRoot(mirrorPath), relativePath), content);

    // The base copy for `relativePath`, or null if there isn't one yet (a file never pulled — created locally).
    public static string? ReadBaseFileIfPresent(string mirrorPath, string relativePath)
    {
        var fullPath = ResolveSafePath(BaseRoot(mirrorPath), relativePath);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    // Every file under the mirror's working tree (AC-282) — `.cockpit-sync/` itself excluded, since that is shadow
    // state, not mirrored content. Relative paths use '/' throughout, matching Depot's own path convention.
    public static IEnumerable<string> EnumerateWorkingFiles(string mirrorPath)
    {
        if (!Directory.Exists(mirrorPath))
        {
            yield break;
        }

        var root = Path.GetFullPath(mirrorPath);
        var syncRoot = SyncRoot(mirrorPath) + Path.DirectorySeparatorChar;
        foreach (var fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (fullPath.StartsWith(syncRoot, StringComparison.Ordinal))
            {
                continue;
            }

            yield return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    // Whether the working file at `relativePath` still has the size/mtime the index recorded when it was last
    // pulled — a stat check, not a re-hash, and the same one git and rsync use to spot a touched file cheaply.
    // A missing working file counts as diverged too: its state ("gone") is not the state the index recorded.
    public static bool HasWorkingFileDiverged(string mirrorPath, ShadowIndexEntry entry)
    {
        var fullPath = ResolveSafePath(mirrorPath, entry.Path);
        if (!File.Exists(fullPath))
        {
            return true;
        }

        var info = new FileInfo(fullPath);
        return info.Length != entry.Size || info.LastWriteTimeUtc != entry.Mtime;
    }

    // Removes both copies of `relativePath` (working tree and base) if present. Used only for a path Depot no
    // longer lists or reports missing — never for a path this pull only failed to read.
    public static void DeleteIfPresent(string mirrorPath, string relativePath)
    {
        _DeleteIfExists(ResolveSafePath(mirrorPath, relativePath));
        _DeleteIfExists(ResolveSafePath(BaseRoot(mirrorPath), relativePath));
    }

    // Depot's path is server-supplied input from this app's point of view — resolved and checked against escaping
    // `root` before anything is read from or written to it, rather than trusted as already safe.
    public static string ResolveSafePath(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (candidate != rootFull && !candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Depot returned a path outside the mirror: '{relativePath}'.");
        }

        return candidate;
    }

    private static void _WriteAtomically(string fullPath, string content)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static void _DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
