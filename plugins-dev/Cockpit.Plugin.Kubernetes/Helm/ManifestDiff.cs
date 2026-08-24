using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Helm;

// What one resource does between two revisions of a release.
internal enum ManifestChangeKind
{
    Created,
    Updated,
    Deleted,
}

// One resource's entry in a manifest diff: what happens to it, and — for an update — the literal lines that differ.
internal sealed record ManifestResourceChange(ManifestChangeKind Change, ManifestDocument Document, string? Diff, int AddedLines, int RemovedLines);

// The difference between two rendered Helm manifests (AC-1061 fase 2), the thing the operator approves a rollback
// on and the thing the rollback then carries out — one computation feeding both, so what was shown is what runs.
internal sealed record ManifestDiff(
    IReadOnlyList<ManifestResourceChange> Changes,
    int UnchangedCount,
    IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => Changes.Count == 0;

    public IEnumerable<ManifestResourceChange> Applied => Changes.Where(change => change.Change != ManifestChangeKind.Deleted);

    public IEnumerable<ManifestResourceChange> Deletions => Changes.Where(change => change.Change == ManifestChangeKind.Deleted);

    // Compares the manifest the release currently has against the one it would have. A resource in `current` and not
    // in `target` is a deletion: helm removes it on a rollback, and leaving it behind would put the cluster in a
    // state that is neither revision.
    public static ManifestDiff Compute(string? currentManifest, string? targetManifest)
    {
        var current = ManifestDocument.SplitAll(currentManifest, out var currentErrors);
        var target = ManifestDocument.SplitAll(targetManifest, out var targetErrors);
        var warnings = new List<string>();
        warnings.AddRange(currentErrors.Select(error => $"Current revision: {error}"));
        warnings.AddRange(targetErrors.Select(error => $"Target revision: {error}"));

        var currentByKey = _Index(current, "current", warnings);
        var targetByKey = _Index(target, "target", warnings);

        var changes = new List<ManifestResourceChange>();
        var unchanged = 0;
        foreach (var document in target)
        {
            if (!currentByKey.TryGetValue(document.Key, out var existing))
            {
                changes.Add(new ManifestResourceChange(ManifestChangeKind.Created, document, null, 0, 0));
            }
            else if (existing.Text == document.Text)
            {
                unchanged++;
            }
            else
            {
                var (diff, added, removed) = ManifestLineDiff.Compute(existing.Text, document.Text);
                changes.Add(new ManifestResourceChange(ManifestChangeKind.Updated, document, diff, added, removed));
            }
        }

        changes.AddRange(current
            .Where(document => !targetByKey.ContainsKey(document.Key))
            .Select(document => new ManifestResourceChange(ManifestChangeKind.Deleted, document, null, 0, 0)));

        return new ManifestDiff(changes, unchanged, warnings);
    }

    // The text the operator reads on the consent card, as one wrapped block. Built from `ToConsentLines` — see
    // there for the bounding rule.
    public string ToConsentText(int maxLength) => string.Join('\n', ToConsentLines(maxLength));

    // The lines the operator reads on the consent card (AC-1062): one element per rendered line, so a gate can
    // escape and join them itself instead of receiving one block with the breaks already baked in as `\n`.
    // Bounded: past `maxLength` it says how much it left out instead of pushing the decision off the card.
    public IReadOnlyList<string> ToConsentLines(int maxLength)
    {
        var lines = new List<string> { _Headline() };
        var length = lines[0].Length;

        // A document that would not parse may hide a resource this rollback should have touched, so it belongs on
        // the card the operator decides from — not only in the result the agent reads afterwards. Not subject to
        // the budget below: a parse warning must never be the thing truncation drops.
        foreach (var warning in Warnings)
        {
            var line = $"! {warning}";
            lines.Add(line);
            length += 1 + line.Length;
        }

        var truncated = 0;
        foreach (var change in Changes)
        {
            var entryLines = change.Change switch
            {
                ManifestChangeKind.Created => new[] { $"+ CREATE {change.Document.Display}" },
                ManifestChangeKind.Deleted => new[] { $"- DELETE {change.Document.Display}" },
                _ => new[] { $"~ UPDATE {change.Document.Display} (+{change.AddedLines}/-{change.RemovedLines})" }
                    .Concat(change.Diff!.Split('\n')).ToArray(),
            };
            var entryLength = entryLines.Sum(line => line.Length) + entryLines.Length;

            if (length + entryLength > maxLength)
            {
                truncated++;
                continue;
            }

            lines.AddRange(entryLines);
            length += entryLength;
        }

        if (truncated > 0)
        {
            lines.Add($"… and {truncated} more resource(s) — read both revisions with helm_manifest to see everything");
        }

        return lines;
    }

    public JsonObject ToJson() => new()
    {
        ["created"] = Changes.Count(change => change.Change == ManifestChangeKind.Created),
        ["updated"] = Changes.Count(change => change.Change == ManifestChangeKind.Updated),
        ["deleted"] = Changes.Count(change => change.Change == ManifestChangeKind.Deleted),
        ["unchanged"] = UnchangedCount,
        ["resources"] = new JsonArray(Changes.Select(change => (JsonNode?)new JsonObject
        {
            ["change"] = change.Change.ToString().ToLowerInvariant(),
            ["resource"] = change.Document.Display,
            ["diff"] = change.Diff,
        }).ToArray()),
        ["warnings"] = new JsonArray(Warnings.Select(warning => (JsonNode?)JsonValue.Create(warning)).ToArray()),
    };

    private string _Headline()
    {
        var created = Changes.Count(change => change.Change == ManifestChangeKind.Created);
        var updated = Changes.Count(change => change.Change == ManifestChangeKind.Updated);
        var deleted = Changes.Count(change => change.Change == ManifestChangeKind.Deleted);
        return $"{created} to create, {updated} to update, {deleted} to DELETE, {UnchangedCount} unchanged";
    }

    // Two documents with the same identity in one manifest cannot both be applied, and silently keeping the last one
    // would hide half of what is about to happen — keep the first and say so.
    private static Dictionary<string, ManifestDocument> _Index(IReadOnlyList<ManifestDocument> documents, string side, List<string> warnings)
    {
        var index = new Dictionary<string, ManifestDocument>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (!index.TryAdd(document.Key, document))
            {
                warnings.Add($"The {side} revision renders {document.Display} more than once; only the first is compared.");
            }
        }

        return index;
    }
}
