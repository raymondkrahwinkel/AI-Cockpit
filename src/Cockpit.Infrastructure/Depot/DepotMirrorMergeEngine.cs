using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Depot;

// Resolves AC-281's diverged files (AC-283): git merge-file --diff3 against the local base (AC-281's shadow
// copy — Depot cannot hand back old bytes non-destructively, so restore_version is never a read route), the
// working copy, and Depot's current content. Reuses GitCli, the worktree git wrapper, rather than a second one.
internal sealed class DepotMirrorMergeEngine : IDepotMirrorMergeEngine, ISingletonService
{
    private readonly IDepotSyncClient _client;

    public DepotMirrorMergeEngine(IDepotSyncClient client)
    {
        _client = client;
    }

    public async Task<DepotMergeResult> MergeAsync(
        DepotMirror mirror, string serverName, string project, IReadOnlyList<DepotDivergedFile> diverged,
        CancellationToken cancellationToken = default)
    {
        if (diverged.Count == 0)
        {
            return DepotMergeResult.Success([], []);
        }

        var index = ShadowSyncStorage.LoadIndex(mirror.Path);
        if (index is null)
        {
            return DepotMergeResult.Failed(
                $"The local shadow index at '{ShadowSyncStorage.SyncRoot(mirror.Path)}' is unreadable. Left untouched rather than " +
                "guessing at a merge base it does not actually record.");
        }

        var merged = new List<string>();
        var conflicted = new List<DepotMergeConflict>();
        var updatedIndex = new Dictionary<string, ShadowIndexEntry>(index, StringComparer.Ordinal);

        // Criterion 2: a base list_versions couldn't confirm (AC-281) is never fed to a merge — reported the
        // same way an unresolved textual conflict is, with nothing on disk touched.
        var confirmedPaths = new List<string>();
        foreach (var file in diverged)
        {
            if (file.BaseConfirmed)
            {
                confirmedPaths.Add(file.Path);
            }
            else
            {
                conflicted.Add(new DepotMergeConflict(
                    file.Path,
                    "The local base could not be confirmed against Depot's version history — no automatic merge attempted."));
            }
        }

        if (confirmedPaths.Count > 0)
        {
            var readResult = await _client.ReadManyAsync(serverName, project, confirmedPaths, cancellationToken).ConfigureAwait(false);
            switch (readResult.Outcome)
            {
                case DepotReadManyOutcome.AuthorizationRequired:
                    return DepotMergeResult.AuthorizationRequired;
                case DepotReadManyOutcome.Failed:
                    return DepotMergeResult.Failed(readResult.Error ?? "Depot did not return file contents.");
            }

            foreach (var path in readResult.Missing)
            {
                conflicted.Add(new DepotMergeConflict(
                    path, "Depot no longer has this file — cannot 3-way merge against a side that disappeared."));
            }

            foreach (var path in readResult.Unreadable)
            {
                conflicted.Add(new DepotMergeConflict(path, "Depot's current content for this file could not be read."));
            }

            var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cockpit-merge-{Guid.NewGuid():n}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                foreach (var remote in readResult.Files)
                {
                    var conflict = await _MergeOneFileAsync(mirror, remote, index, updatedIndex, temporaryDirectory, cancellationToken)
                        .ConfigureAwait(false);

                    if (conflict is null)
                    {
                        merged.Add(remote.Path);
                    }
                    else if (conflict.Reason.StartsWith(_GitFailurePrefix, StringComparison.Ordinal))
                    {
                        // git itself could not run at all (missing binary, killed process) — not a per-file
                        // conflict but a reason to stop the whole round rather than mislabel every remaining file.
                        return DepotMergeResult.Failed(conflict.Reason[_GitFailurePrefix.Length..]);
                    }
                    else
                    {
                        conflicted.Add(conflict);
                    }
                }
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        ShadowSyncStorage.SaveIndex(mirror.Path, updatedIndex);
        return DepotMergeResult.Success(merged, conflicted);
    }

    private const string _GitFailurePrefix = "git-unavailable:";

    // One confirmed-diverged file's 3-way merge. Returns null on a clean merge (the caller records it as merged);
    // otherwise a conflict to report — except a `_GitFailurePrefix`-tagged one, which means git itself could not
    // be run and the whole round should stop rather than silently skip the rest.
    private static async Task<DepotMergeConflict?> _MergeOneFileAsync(
        DepotMirror mirror, DepotReadFile remote, IReadOnlyDictionary<string, ShadowIndexEntry> index,
        Dictionary<string, ShadowIndexEntry> updatedIndex, string temporaryDirectory, CancellationToken cancellationToken)
    {
        var path = remote.Path;
        var baseContent = ShadowSyncStorage.ReadBaseFileIfPresent(mirror.Path, path);
        var workingPath = ShadowSyncStorage.ResolveSafePath(mirror.Path, path);

        if (baseContent is null || !File.Exists(workingPath) || !index.TryGetValue(path, out var oldEntry))
        {
            return new DepotMergeConflict(path, "No local base copy, working file, or shadow index entry exists for this file — cannot 3-way merge it.");
        }

        var localContent = File.ReadAllText(workingPath);

        // Text recognition is on content, not extension (the ticket's own NUL-byte heuristic) — the whole
        // pull/push pipeline already only ever moves file bytes as decoded strings, so an embedded NUL survives
        // the round trip unchanged.
        if (localContent.Contains('\0') || baseContent.Contains('\0') || remote.Content.Contains('\0'))
        {
            return new DepotMergeConflict(
                path, "Binary content — automatic \"newest wins\" needs Depot's file-time semantics, which AC-283 leaves undecided.");
        }

        var localFile = Path.Combine(temporaryDirectory, "local");
        var baseFile = Path.Combine(temporaryDirectory, "base");
        var remoteFile = Path.Combine(temporaryDirectory, "depot");
        File.WriteAllText(localFile, localContent);
        File.WriteAllText(baseFile, baseContent);
        File.WriteAllText(remoteFile, remote.Content);

        GitResult mergeResult;
        try
        {
            mergeResult = await GitCli.RunAsync(
                mirror.Path,
                ["merge-file", "-p", "--diff3", "-L", "local", "-L", "base (last sync)", "-L", "depot", localFile, baseFile, remoteFile],
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return new DepotMergeConflict(path, $"{_GitFailurePrefix}{exception.Message}");
        }

        if (mergeResult.ExitCode == 0)
        {
            ShadowSyncStorage.WriteWorkingFile(mirror.Path, path, mergeResult.StandardOutput);
            ShadowSyncStorage.WriteBaseFile(mirror.Path, path, remote.Content);
            updatedIndex[path] = oldEntry with { BaseChecksum = remote.Checksum ?? string.Empty };
            return null;
        }

        if (mergeResult.ExitCode > 0)
        {
            // Conflict markers only — base/index are left exactly as they were. Updating either here would make
            // the next push an overwrite instead of the conflict it still is.
            ShadowSyncStorage.WriteWorkingFile(mirror.Path, path, mergeResult.StandardOutput);
            return new DepotMergeConflict(path, "git merge-file found conflicting changes — resolve the markers in the file.");
        }

        return new DepotMergeConflict(path, $"git merge-file failed: {GitCli.StripProgress(mergeResult.StandardError)}");
    }
}
