using System.Text.Json.Nodes;

namespace Cockpit.Core.Backup;

// AC-695: re-anchors the absolute paths a restored `cockpit.json` carries onto the machine restoring it. AC-605's
// shape — an anchor plus the target's own root — but not its code: `ProjectResourcePathPortability` resolves through
// `System.IO.Path`, which understands only the platform it runs on. So the anchor is matched as plain text.
public static class RestorePathPortability
{
    // Rewrites every path under `sourceConfigRoot` in `settings` to sit under `targetConfigRoot` instead, and hands
    // back the project folders that lie outside it and do not exist here — those are never guessed at. A null
    // `sourceConfigRoot` means no anchor is known, so nothing is rewritten; the report is still made.
    public static IReadOnlyList<string> Rebase(JsonObject settings, string? sourceConfigRoot, string targetConfigRoot)
    {
        if (!string.IsNullOrWhiteSpace(sourceConfigRoot))
        {
            _RebaseUnder(settings, sourceConfigRoot.TrimEnd('/', '\\'), targetConfigRoot.TrimEnd('/', '\\'));
        }

        return _UnresolvedProjectFolders(settings);
    }

    // Every string in the tree is offered rather than a list of the fields that hold paths: a value starting with the
    // backup machine's own config root is a path into it whoever wrote it — the logos, the worktree and clone
    // registries, whatever a plugin stored there — and a list of field names is a list that goes stale.
    private static void _RebaseUnder(JsonNode? node, string sourceRoot, string targetRoot)
    {
        switch (node)
        {
            case JsonObject settings:
                // The keys are taken first: assigning into a JsonObject while enumerating it throws.
                foreach (var name in settings.Select(property => property.Key).ToList())
                {
                    if (_RebasedOrNull(settings[name], sourceRoot, targetRoot) is { } rebased)
                    {
                        settings[name] = rebased;
                    }
                    else
                    {
                        _RebaseUnder(settings[name], sourceRoot, targetRoot);
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (_RebasedOrNull(array[index], sourceRoot, targetRoot) is { } rebased)
                    {
                        array[index] = rebased;
                    }
                    else
                    {
                        _RebaseUnder(array[index], sourceRoot, targetRoot);
                    }
                }

                break;
        }
    }

    private static string? _RebasedOrNull(JsonNode? node, string sourceRoot, string targetRoot)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || !_IsUnder(text, sourceRoot))
        {
            return null;
        }

        // Split on both separators and rebuilt with Path.Combine: the archive carries the writing platform's
        // separator, and the restored file must carry this one's. The separators go in as an array on purpose —
        // `Split('/', '\\', options)` binds to the (separator, count, options) overload, with '\\' read as a count.
        return Path.Combine([targetRoot, .. text[sourceRoot.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);
    }

    // Case-insensitively on both platforms: a config root differing from a stored path only in casing is a Windows
    // certainty and a Linux impossibility, so ignoring case can only ever match the path that was meant.
    private static bool _IsUnder(string text, string sourceRoot) =>
        text.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
        && (text.Length == sourceRoot.Length || text[sourceRoot.Length] is '/' or '\\');

    // Named as "<project> → <folder>", and the value stays in the file: a visibly wrong path can be corrected, a
    // silently dropped or repointed one cannot. Projects only — a `worktrees` or `clones` record whose folder is
    // gone is already forgotten by its own manager (`RepositoryCloneManager.ReconcileAsync`, `WorktreeManager`).
    private static IReadOnlyList<string> _UnresolvedProjectFolders(JsonObject settings)
    {
        var unresolved = new List<string>();

        foreach (var project in settings["Projects"] as JsonArray ?? [])
        {
            if (project is not JsonObject entry)
            {
                continue;
            }

            var name = _Text(entry["Name"]) ?? _Text(entry["Id"]) ?? "an unnamed project";

            unresolved.AddRange(_FoldersOf(entry)
                .Where(folder => !Directory.Exists(folder))
                .Select(folder => $"{name} \u2192 {folder}"));
        }

        return unresolved;
    }

    private static IEnumerable<string> _FoldersOf(JsonObject entry)
    {
        var folders = new List<string>();

        if (_Text(entry["SourceDirectory"]) is { } single)
        {
            folders.Add(single);
        }

        foreach (var repository in entry["SourceDirectories"] as JsonArray ?? [])
        {
            if (_Text(repository?["Path"]) is { } path)
            {
                folders.Add(path);
            }
        }

        // The legacy SourceDirectory mirrors SourceDirectories[0].Path, so one folder would be reported twice.
        return folders.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static string? _Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
