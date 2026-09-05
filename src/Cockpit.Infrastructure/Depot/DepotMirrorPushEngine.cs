using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Depot;

// Pushes a Depot mirror's local changes up (AC-282): every write carries the shadow index's recorded
// baseChecksum, so write_many alone decides written/conflict/invalid. A file pull left Diverged or Retained
// still carries its old baseChecksum like any other; write_many's own conflict on that is what stops a revival.
internal sealed class DepotMirrorPushEngine : IDepotMirrorPushEngine, ISingletonService
{
    private readonly IDepotSyncClient _client;

    public DepotMirrorPushEngine(IDepotSyncClient client)
    {
        _client = client;
    }

    public async Task<DepotPushResult> PushAsync(
        DepotMirror mirror, string serverName, string project, CancellationToken cancellationToken = default)
    {
        var index = ShadowSyncStorage.LoadIndex(mirror.Path);
        if (index is null)
        {
            return DepotPushResult.Failed(
                $"The local shadow index at '{ShadowSyncStorage.SyncRoot(mirror.Path)}' is unreadable. Left untouched rather than " +
                "guessing which local files are unchanged, which could push over a conflict that index was the only record of.");
        }

        var candidates = new List<(string Path, string Content, string? BaseChecksum, long Size, DateTimeOffset Mtime)>();

        foreach (var path in ShadowSyncStorage.EnumerateWorkingFiles(mirror.Path))
        {
            var isNew = !index.TryGetValue(path, out var entry);
            if (!isNew && !ShadowSyncStorage.HasWorkingFileDiverged(mirror.Path, entry!))
            {
                continue;
            }

            var fullPath = ShadowSyncStorage.ResolveSafePath(mirror.Path, path);
            var content = File.ReadAllText(fullPath);
            var info = new FileInfo(fullPath);

            if (!isNew)
            {
                // Doubtful case (criterion 1): size/mtime moved but that alone doesn't prove the content did.
                // Confirmed against the recorded base bytes directly — both are already in memory to push it.
                var baseContent = ShadowSyncStorage.ReadBaseFileIfPresent(mirror.Path, path);
                if (baseContent == content)
                {
                    continue;
                }
            }

            candidates.Add((path, content, entry?.BaseChecksum, info.Length, info.LastWriteTimeUtc));
        }

        if (candidates.Count == 0)
        {
            return DepotPushResult.Success([], [], [], []);
        }

        var writeResult = await _client.WriteManyAsync(
            serverName, project,
            candidates.Select(c => new DepotWriteEntry(c.Path, c.Content, c.BaseChecksum)).ToList(),
            cancellationToken).ConfigureAwait(false);

        var byPath = candidates.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var updatedIndex = new Dictionary<string, ShadowIndexEntry>(index, StringComparer.Ordinal);
        var pushed = new List<string>();
        var conflicted = new List<string>();
        var invalid = new List<string>();
        var failed = new List<string>();

        foreach (var result in writeResult.Results)
        {
            switch (result.Status)
            {
                case DepotWriteStatus.Written:
                    var candidate = byPath[result.Path];
                    ShadowSyncStorage.WriteBaseFile(mirror.Path, result.Path, candidate.Content);
                    updatedIndex[result.Path] = new ShadowIndexEntry(
                        result.Path, result.Checksum ?? string.Empty, candidate.Size, candidate.Mtime);
                    pushed.Add(result.Path);
                    break;
                case DepotWriteStatus.Conflict:
                    conflicted.Add(result.Path);
                    break;
                case DepotWriteStatus.Invalid:
                    invalid.Add(result.Path);
                    break;
                default:
                    failed.Add(result.Path);
                    break;
            }
        }

        ShadowSyncStorage.SaveIndex(mirror.Path, updatedIndex);

        return DepotPushResult.Success(pushed, conflicted, invalid, failed);
    }
}
