using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.Publishing;

// What the last publication left in Depot for one file, kept in the plugin cache under the link's id. `Checksum` is
// Depot's own: SHA-256, lowercase hex, over the raw bytes. A later two-way sync adds its shadow base to this same entry.
internal sealed record PublishManifestEntry(string Path, string Kind, string Checksum, long Size, DateTimeOffset Mtime);

// AC-1574: one-way publishing of a local folder or file to a folder in a Depot project. Plan, then a run the operator
// confirmed. The manifest decides what is ours to change or delete in Depot; nothing outside it is ever touched.
internal sealed class DepotPublishService(DepotMcp mcp, DepotSettings settings, IPluginCache cache, HttpClient http)
{
    // ponytail: the most Depot's /sync/files lists in one page; page the listing when a real source needs more.
    internal const int MaxFiles = 1000;

    private const int PageSize = 500;
    private const int MaxPages = 100;

    private sealed class _ConflictException(string message) : Exception(message);

    private sealed record _Local(string Relative, string FullPath, string Kind, long Size, DateTimeOffset Mtime, string? Checksum, string? Skip);

    private sealed record _Step(DepotPublishItem Item, _Local? Local, string? Remote);

    private readonly DepotUploader _uploader = new(mcp);
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotPublicationRow>>> ListAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotMcp.RunAsync(() => Task.FromResult<IReadOnlyList<DepotPublicationRow>>([.. settings.Publications
            .Where(link => link.ConnectionId == request.ConnectionId && (request.Project is null || link.Project == request.Project))
            .Select(link => new DepotPublicationRow(link, cache.Get<DepotPublishReport>(_LastKey(link.Id))))]));

    public Task<DepotBrowserAnswer<DepotPublication>> AddAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotMcp.RunAsync(async () =>
        {
            var project = DepotMcp.Required(request.Project, "project");
            var source = DepotMcp.Required(request.Source, "source");
            await mcp.RequireEditorAsync(request.ConnectionId, project, "Publishing", cancellationToken).ConfigureAwait(false);
            if (!Path.IsPathRooted(source))
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "The source must be an absolute path on this machine.");
            }

            source = Path.TrimEndingDirectorySeparator(source.Trim());
            if (!Directory.Exists(source) && !File.Exists(source))
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "That folder or file does not exist on this machine.");
            }

            var target = _NormalizeTarget(request.TargetFolder);
            if (settings.Publications.Any(link => link.ConnectionId == request.ConnectionId && link.Project == project && link.Source == source && link.TargetFolder == target))
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "That source is already published to that folder.");
            }

            var added = new DepotPublication(Guid.NewGuid().ToString("n"), request.ConnectionId, project, source, target);
            settings.Publications = [.. settings.Publications, added];
            return added;
        });

    // Depot keeps nothing of ours, so removing the link only forgets the manifest.
    public Task<DepotBrowserAnswer<bool>> RemoveAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotMcp.RunAsync(async () =>
        {
            var link = await _LinkAsync(request, cancellationToken).ConfigureAwait(false);
            settings.Publications = [.. settings.Publications.Where(candidate => candidate.Id != link.Id)];
            _WriteManifest(link.Id, new Dictionary<string, PublishManifestEntry>());
            cache.Set<DepotPublishReport?>(_LastKey(link.Id), null);
            return true;
        });

    public Task<DepotBrowserAnswer<DepotPublishPlan>> PlanAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotMcp.RunAsync(async () =>
        {
            var link = await _LinkAsync(request, cancellationToken).ConfigureAwait(false);
            return new DepotPublishPlan([.. (await _BuildAsync(link, cancellationToken).ConfigureAwait(false)).Select(step => step.Item)]);
        });

    public Task<DepotBrowserAnswer<DepotPublishReport>> RunAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotMcp.RunAsync(async () =>
        {
            var link = await _LinkAsync(request, cancellationToken).ConfigureAwait(false);
            if (!request.Confirmed)
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "Confirm the plan before publishing.");
            }

            if (!_running.TryAdd(link.Id, true))
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "This publication is already running.");
            }

            try
            {
                return await _ExecuteAsync(link, request.OverwriteConflicts, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(link.Id, out _);
            }
        });

    // What a file is, given what is in Depot and what the last publication left there. The same table a two-way sync
    // starts from: Depot equal to us is nothing to write, Depot equal to the manifest is ours to replace, anything else is theirs.
    internal static string Classify(string local, string? remote, string? known) =>
        remote is null ? DepotPublishAction.New
        : remote == local ? (known == local ? DepotPublishAction.Unchanged : DepotPublishAction.Adopt)
        : known is not null && remote == known ? DepotPublishAction.Changed
        : DepotPublishAction.Conflict;

    private async Task<DepotPublication> _LinkAsync(DepotPublishRequest request, CancellationToken cancellationToken)
    {
        var link = settings.Publications.FirstOrDefault(candidate => candidate.Id == request.Id && candidate.ConnectionId == request.ConnectionId)
            ?? throw new DepotFailure(DepotBrowserStatus.Error, "That publication no longer exists.");
        await mcp.RequireEditorAsync(link.ConnectionId, link.Project, "Publishing", cancellationToken).ConfigureAwait(false);
        return link;
    }

    private async Task<IReadOnlyList<_Step>> _BuildAsync(DepotPublication link, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(link.Source) && !File.Exists(link.Source))
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "The source does not exist on this machine.");
        }

        var manifest = _ReadManifest(link.Id);
        var locals = await Task.Run(() => _Scan(link.Source, manifest, cancellationToken), cancellationToken).ConfigureAwait(false);
        var (documents, artifacts) = await _RemoteAsync(link, cancellationToken).ConfigureAwait(false);
        string? RemoteOf(string kind, string relative) =>
            (kind == DepotBrowserKind.Document ? documents : artifacts).GetValueOrDefault(_DepotPath(link, relative));

        var steps = new List<_Step>();
        foreach (var local in locals)
        {
            if (local.Skip is not null || local.Checksum is null)
            {
                steps.Add(new _Step(new DepotPublishItem(local.Relative, local.Kind, DepotPublishAction.Skipped, local.Skip), local, null));
                continue;
            }

            var remote = RemoteOf(local.Kind, local.Relative);
            var action = Classify(local.Checksum, remote, manifest.GetValueOrDefault(local.Relative)?.Checksum);
            var detail = action == DepotPublishAction.Conflict ? "Changed in Depot since the last publication." : null;
            steps.Add(new _Step(new DepotPublishItem(local.Relative, local.Kind, action, detail), local, remote));
        }

        // Only what this link published itself can be deleted: the loop runs over the manifest, never over Depot's listing.
        var present = locals.Select(local => local.Relative).ToHashSet(StringComparer.Ordinal);
        foreach (var known in manifest.Values.Where(entry => !present.Contains(entry.Path)).OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            var remote = RemoteOf(known.Kind, known.Path);
            var untouched = remote is null || remote == known.Checksum;
            steps.Add(new _Step(
                new DepotPublishItem(known.Path, known.Kind, untouched ? DepotPublishAction.Delete : DepotPublishAction.DeleteConflict, untouched ? null : "Changed in Depot since the last publication."),
                null,
                remote));
        }

        return steps;
    }

    private async Task<DepotPublishReport> _ExecuteAsync(DepotPublication link, bool overwriteConflicts, CancellationToken cancellationToken)
    {
        var steps = await _BuildAsync(link, cancellationToken).ConfigureAwait(false);
        var manifest = _ReadManifest(link.Id);
        int written = 0, adopted = 0, deleted = 0, unchanged = 0;
        var problems = new List<DepotPublishItem>();
        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = step.Item;
                try
                {
                    switch (item.Action)
                    {
                        case DepotPublishAction.Unchanged:
                            manifest[item.Path] = _Entry(step.Local!, step.Local!.Checksum!);
                            unchanged++;
                            break;
                        case DepotPublishAction.Adopt:
                            manifest[item.Path] = _Entry(step.Local!, step.Remote!);
                            adopted++;
                            break;
                        case DepotPublishAction.New or DepotPublishAction.Changed:
                            manifest[item.Path] = await _WriteAsync(link, step, cancellationToken).ConfigureAwait(false);
                            written++;
                            break;
                        case DepotPublishAction.Conflict when overwriteConflicts:
                            manifest[item.Path] = await _WriteAsync(link, step, cancellationToken).ConfigureAwait(false);
                            written++;
                            break;
                        case DepotPublishAction.Delete:
                            if (await _DeleteAsync(link, step, cancellationToken).ConfigureAwait(false))
                            {
                                deleted++;
                            }

                            manifest.Remove(item.Path);
                            break;
                        default:
                            problems.Add(item);
                            break;
                    }
                }
                catch (_ConflictException conflict)
                {
                    problems.Add(item with { Action = DepotPublishAction.Conflict, Detail = conflict.Message });
                }
                catch (DepotFailure failure) when (failure.Status != DepotBrowserStatus.SignIn)
                {
                    var action = failure.Message.StartsWith("[Conflict]", StringComparison.Ordinal) ? DepotPublishAction.Conflict : DepotPublishAction.Failed;
                    problems.Add(item with { Action = action, Detail = failure.Message });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    problems.Add(item with { Action = DepotPublishAction.Failed, Detail = "The file could not be read." });
                }
            }
        }
        finally
        {
            _WriteManifest(link.Id, manifest);
        }

        var report = new DepotPublishReport(DateTimeOffset.UtcNow, written, adopted, deleted, unchanged, problems);
        cache.Set<DepotPublishReport?>(_LastKey(link.Id), report);
        return report;
    }

    // The manifest entry is returned only once Depot has the bytes, so a failed write leaves the path out of it.
    private async Task<PublishManifestEntry> _WriteAsync(DepotPublication link, _Step step, CancellationToken cancellationToken)
    {
        var local = step.Local!;
        var depotPath = _DepotPath(link, local.Relative);
        if (local.Kind == DepotBrowserKind.Document)
        {
            var bytes = await File.ReadAllBytesAsync(local.FullPath, cancellationToken).ConfigureAwait(false);
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            var written = await _uploader.StartAsync(link.ConnectionId, link.Project, depotPath, text, string.Empty, bytes.Length, step.Remote is { Length: > 0 } remote ? remote : null, cancellationToken).ConfigureAwait(false);
            return new PublishManifestEntry(local.Relative, local.Kind, written.Checksum ?? _Sha(bytes), bytes.Length, local.Mtime);
        }

        await using var stream = new FileStream(local.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var contentType = DepotUploadKinds.ContentType(local.Relative);
        var length = stream.Length;
        var ticket = await _uploader.StartAsync(link.ConnectionId, link.Project, depotPath, null, contentType, length, null, cancellationToken).ConfigureAwait(false);
        await _RecheckArtifactAsync(link, depotPath, step.Remote, cancellationToken).ConfigureAwait(false);
        var checksum = await DepotUploader.PutAsync(
            http, ticket.UploadUrl ?? throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return an upload URL."), stream, contentType, cancellationToken).ConfigureAwait(false);
        return new PublishManifestEntry(local.Relative, local.Kind, checksum ?? local.Checksum!, length, local.Mtime);
    }

    // request_upload has no baseChecksum, so the plan's checksum is checked once more right before the PUT.
    // ponytail: a race of milliseconds remains, until Depot takes a baseChecksum on the upload.
    private async Task _RecheckArtifactAsync(DepotPublication link, string depotPath, string? expected, CancellationToken cancellationToken)
    {
        using var listing = DepotMcp.Parse(await mcp.CallAsync(
            link.ConnectionId,
            "list_artifacts",
            new Dictionary<string, object?> { ["project"] = link.Project, ["path"] = depotPath, ["includeChecksums"] = true },
            cancellationToken).ConfigureAwait(false));
        var current = DepotMcp.Entries(listing.RootElement)
            .Where(entry => DepotMcp.String(entry, "path") == depotPath)
            .Select(entry => DepotMcp.String(entry, "checksum") ?? string.Empty)
            .FirstOrDefault();
        if (current != expected)
        {
            throw new _ConflictException("Changed in Depot since the plan.");
        }
    }

    // Soft, and guarded by the checksum the manifest holds. False when Depot no longer had the file.
    private async Task<bool> _DeleteAsync(DepotPublication link, _Step step, CancellationToken cancellationToken)
    {
        if (step.Remote is null)
        {
            return false;
        }

        try
        {
            await mcp.CallAsync(
                link.ConnectionId,
                "delete",
                new Dictionary<string, object?>
                {
                    ["project"] = link.Project,
                    ["path"] = _DepotPath(link, step.Item.Path),
                    ["kind"] = step.Item.Kind == DepotBrowserKind.Document ? "memory" : "artifact",
                    ["hard"] = false,
                    ["baseChecksum"] = step.Remote,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DepotFailure failure) when (failure.Message.StartsWith("[NotFound]", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private async Task<(Dictionary<string, string> Documents, Dictionary<string, string> Artifacts)> _RemoteAsync(
        DepotPublication link, CancellationToken cancellationToken)
    {
        var prefix = link.TargetFolder.Length == 0 ? string.Empty : link.TargetFolder + "/";
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var arguments = new Dictionary<string, object?> { ["project"] = link.Project, ["includeChecksums"] = true, ["limit"] = PageSize };
            if (link.TargetFolder.Length > 0)
            {
                arguments["path"] = link.TargetFolder;
            }

            if (cursor is not null)
            {
                arguments["after"] = cursor;
            }

            using var listing = DepotMcp.Parse(await mcp.CallAsync(link.ConnectionId, "list", arguments, cancellationToken).ConfigureAwait(false));
            _Collect(listing.RootElement, prefix, documents);
            var next = DepotMcp.String(listing.RootElement, "nextCursor");
            if (string.IsNullOrEmpty(next) || next == cursor)
            {
                break;
            }

            cursor = next;
        }

        var artifactArguments = new Dictionary<string, object?> { ["project"] = link.Project, ["includeChecksums"] = true };
        if (link.TargetFolder.Length > 0)
        {
            artifactArguments["path"] = link.TargetFolder;
        }

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        using var artifactListing = DepotMcp.Parse(await mcp.CallAsync(link.ConnectionId, "list_artifacts", artifactArguments, cancellationToken).ConfigureAwait(false));
        _Collect(artifactListing.RootElement, prefix, artifacts);
        return (documents, artifacts);
    }

    // A listing without a checksum still counts as present, with one nothing equals: it can only become a conflict.
    private static void _Collect(System.Text.Json.JsonElement root, string prefix, Dictionary<string, string> into)
    {
        foreach (var entry in DepotMcp.Entries(root))
        {
            if (DepotMcp.String(entry, "path") is { } path && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                into[path] = DepotMcp.String(entry, "checksum") ?? string.Empty;
            }
        }
    }

    // Dot entries and links are left out without a word; a file Depot and Cockpit would read as different kinds is reported.
    private static List<_Local> _Scan(string source, IReadOnlyDictionary<string, PublishManifestEntry> manifest, CancellationToken cancellationToken)
    {
        var found = new List<_Local>();
        if (File.Exists(source))
        {
            var file = new FileInfo(source);
            if (!_Excluded(file))
            {
                found.Add(_Describe(file, file.Name, manifest));
            }

            return found;
        }

        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((new DirectoryInfo(source), string.Empty));
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(candidate => candidate.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_Excluded(entry))
                {
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push((child, prefix + child.Name + "/"));
                    continue;
                }

                found.Add(_Describe((FileInfo)entry, prefix + entry.Name, manifest));
                if (found.Count > MaxFiles)
                {
                    throw new DepotFailure(DepotBrowserStatus.Error, $"This source holds more than {MaxFiles} files. Publish a smaller folder.");
                }
            }
        }

        return [.. found.OrderBy(local => local.Relative, StringComparer.Ordinal)];
    }

    private static bool _Excluded(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null;

    private static _Local _Describe(FileInfo file, string relative, IReadOnlyDictionary<string, PublishManifestEntry> manifest)
    {
        var kind = DepotUploadKinds.IsText(relative) ? DepotBrowserKind.Document : DepotBrowserKind.Artifact;
        var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        string? skip = null;
        if (relative.Any(char.IsControl) || relative.Contains('\\'))
        {
            skip = "The name has characters Depot does not accept.";
        }
        else if (Path.GetExtension(relative).Length == 0)
        {
            skip = "No extension, so Depot and Cockpit would not agree on text or binary.";
        }
        else if (file.Length > (kind == DepotBrowserKind.Document ? DepotUploadKinds.MaxInlineBytes : DepotUploadKinds.MaxBlobBytes))
        {
            skip = kind == DepotBrowserKind.Document ? "A text file can be at most 8 MiB." : "A file can be at most 100 MiB.";
        }

        if (skip is not null)
        {
            return new _Local(relative, file.FullName, kind, file.Length, mtime, null, skip);
        }

        if (manifest.TryGetValue(relative, out var known) && known.Size == file.Length && known.Mtime == mtime)
        {
            return new _Local(relative, file.FullName, kind, file.Length, mtime, known.Checksum, null);
        }

        try
        {
            if (kind == DepotBrowserKind.Document)
            {
                var bytes = File.ReadAllBytes(file.FullName);
                _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
                return new _Local(relative, file.FullName, kind, bytes.Length, mtime, _Sha(bytes), null);
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new _Local(relative, file.FullName, kind, stream.Length, mtime, Convert.ToHexStringLower(SHA256.HashData(stream)), null);
        }
        catch (DecoderFallbackException)
        {
            return new _Local(relative, file.FullName, kind, file.Length, mtime, null, "Not UTF-8 text, which is the only text Depot stores as a document.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new _Local(relative, file.FullName, kind, file.Length, mtime, null, "The file could not be read.");
        }
    }

    private static PublishManifestEntry _Entry(_Local local, string checksum) =>
        new(local.Relative, local.Kind, checksum, local.Size, local.Mtime);

    private static string _Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string _DepotPath(DepotPublication link, string relative) =>
        link.TargetFolder.Length == 0 ? relative : $"{link.TargetFolder}/{relative}";

    // Depot reserves dot folders (.trash, .versions, .archive), and `..` would leave the project's tree.
    private static string _NormalizeTarget(string? folder)
    {
        var segments = (folder ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment.StartsWith('.') || segment.Any(char.IsControl)))
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "A target folder cannot contain a name that starts with a dot.");
        }

        return string.Join('/', segments);
    }

    private Dictionary<string, PublishManifestEntry> _ReadManifest(string id) =>
        (cache.Get<List<PublishManifestEntry>>(_ManifestKey(id)) ?? [])
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private void _WriteManifest(string id, Dictionary<string, PublishManifestEntry> manifest) =>
        cache.Set(_ManifestKey(id), manifest.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList());

    private static string _ManifestKey(string id) => $"publish-manifest:{id}";

    private static string _LastKey(string id) => $"publish-last:{id}";
}
