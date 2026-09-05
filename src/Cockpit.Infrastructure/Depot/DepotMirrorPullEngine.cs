using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Depot;

// Pulls a Depot mirror's memory tree onto disk (AC-281): fetches a plain remote change, removes a path Depot no
// longer lists, and leaves a path changed on *both* sides untouched for AC-282/283's merge — only checking it
// against Depot's version history so that later ticket knows whether its recorded base still holds.
internal sealed class DepotMirrorPullEngine : IDepotMirrorPullEngine, ISingletonService
{
    private readonly IDepotSyncClient _client;

    public DepotMirrorPullEngine(IDepotSyncClient client)
    {
        _client = client;
    }

    public async Task<DepotPullResult> PullAsync(
        DepotMirror mirror, string serverName, string project, CancellationToken cancellationToken = default)
    {
        var listResult = await _client.ListAllAsync(serverName, project, cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (listResult.Outcome)
        {
            case DepotListOutcome.AuthorizationRequired:
                return DepotPullResult.AuthorizationRequired;
            case DepotListOutcome.Failed:
                return DepotPullResult.Failed(listResult.Error ?? "Depot did not return a listing.");
        }

        var index = ShadowSyncStorage.LoadIndex(mirror.Path);
        if (index is null)
        {
            return DepotPullResult.Failed(
                $"The local shadow index at '{ShadowSyncStorage.SyncRoot(mirror.Path)}' is unreadable. Left untouched rather than " +
                "guessing it started empty, which could re-pull over a local edit it was the only record of.");
        }

        var remoteFiles = listResult.Files!;
        var remoteByPath = remoteFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);

        var toFetch = new List<string>();
        var toDelete = new List<string>(index.Keys.Where(path => !remoteByPath.ContainsKey(path)));
        var diverged = new List<DepotDivergedFile>();

        foreach (var remote in remoteFiles)
        {
            if (!index.TryGetValue(remote.Path, out var entry))
            {
                toFetch.Add(remote.Path);
                continue;
            }

            if (string.Equals(remote.Checksum, entry.BaseChecksum, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ShadowSyncStorage.HasWorkingFileDiverged(mirror.Path, entry))
            {
                toFetch.Add(remote.Path);
                continue;
            }

            // Both sides changed since the last synced base. Confirm the recorded base is still one Depot
            // actually knows about before this ever reaches AC-283's merge route — never silently proceed either
            // way when that can't be confirmed.
            var confirmed = await _IsBaseConfirmedAsync(serverName, project, remote.Path, entry.BaseChecksum, cancellationToken)
                .ConfigureAwait(false);
            diverged.Add(new DepotDivergedFile(remote.Path, confirmed));
        }

        var readResult = toFetch.Count == 0
            ? DepotReadManyResult.Success([], [], [])
            : await _client.ReadManyAsync(serverName, project, toFetch, cancellationToken).ConfigureAwait(false);

        if (readResult.Outcome == DepotReadManyOutcome.AuthorizationRequired)
        {
            return DepotPullResult.AuthorizationRequired;
        }

        if (readResult.Outcome == DepotReadManyOutcome.Failed)
        {
            return DepotPullResult.Failed(readResult.Error ?? "Depot did not return file contents.");
        }

        // Only what actually gets placed on disk below ever changes this copy — a path whose read failed
        // outright (Unreadable) simply never reaches it, so its earlier base/index pair is written back untouched.
        var updatedIndex = new Dictionary<string, ShadowIndexEntry>(index, StringComparer.Ordinal);
        var pulled = new List<string>();

        foreach (var file in readResult.Files)
        {
            var (size, mtime) = ShadowSyncStorage.WriteWorkingFile(mirror.Path, file.Path, file.Content);
            ShadowSyncStorage.WriteBaseFile(mirror.Path, file.Path, file.Content);
            updatedIndex[file.Path] = new ShadowIndexEntry(file.Path, file.Checksum ?? string.Empty, size, mtime);
            pulled.Add(file.Path);
        }

        var deleted = new List<string>();
        var retained = new List<string>();
        foreach (var path in toDelete.Concat(readResult.Missing).Distinct(StringComparer.Ordinal))
        {
            // Depot no longer has this file — but deleting is the hardest possible edit to a working copy that
            // has itself changed since the synced base, exactly like the diverged-content branch above. Left
            // untouched (base/index pair included) rather than destroying an edit with no way back.
            if (index.TryGetValue(path, out var entry) && ShadowSyncStorage.HasWorkingFileDiverged(mirror.Path, entry))
            {
                retained.Add(path);
                continue;
            }

            ShadowSyncStorage.DeleteIfPresent(mirror.Path, path);
            updatedIndex.Remove(path);
            deleted.Add(path);
        }

        ShadowSyncStorage.SaveIndex(mirror.Path, updatedIndex);

        return DepotPullResult.Success(pulled, deleted, retained, readResult.Unreadable, diverged);
    }

    // Called once the listing checksum is already known to differ from the recorded base, so only Depot's
    // version history can still confirm it. Any failure to ask reads as "not confirmed" — never "assume it's fine".
    private async Task<bool> _IsBaseConfirmedAsync(
        string serverName, string project, string path, string baseChecksum, CancellationToken cancellationToken)
    {
        var versions = await _client.ListVersionsAsync(serverName, project, path, cancellationToken).ConfigureAwait(false);
        return versions.Outcome == DepotListVersionsOutcome.Success
            && versions.Versions is { } known
            && known.Any(version => string.Equals(version.Checksum, baseChecksum, StringComparison.Ordinal));
    }
}
