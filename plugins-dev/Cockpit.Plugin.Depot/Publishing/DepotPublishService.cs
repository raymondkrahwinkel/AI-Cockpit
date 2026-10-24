using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.Publishing;

// What the last publication left in Depot for one file, kept in the plugin cache under the link's id. `Checksum` is
// Depot's own: SHA-256, lowercase hex, over the raw bytes. A later two-way sync adds its shadow base to this same entry.
internal sealed record PublishManifestEntry(string Path, string Kind, string Checksum, long Size, DateTimeOffset Mtime);

// AC-1574: one-way publishing of a local folder or file to a folder in a Depot project. Plan, then a run the operator
// confirmed. The manifest decides what is ours to change or delete in Depot; nothing outside it is ever touched.
internal sealed class DepotPublishService(
    ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    DepotSettings settings,
    IPluginCache cache,
    HttpClient http)
{
    // ponytail: the most Depot's /sync/files lists in one page; page the listing when a real source needs more.
    internal const int MaxFiles = 1000;

    private const int PageSize = 500;
    private const int MaxPages = 100;

    private sealed class _ConflictException(string message) : Exception(message);

    private sealed record _Local(string Relative, string FullPath, string Kind, long Size, DateTimeOffset Mtime, string? Checksum, string? Skip);

    private sealed record _Step(DepotPublishItem Item, _Local? Local, string? Remote);

    private sealed record _DocumentWrite(PublishManifestEntry? Entry, string? Action, string? Detail);

    private readonly ICockpitHost _host = host;
    private readonly Func<IReadOnlyList<DepotConnectionRegistration>> _connections = connections;
    private readonly DepotRequests _mcp = new(host, connections);
    private readonly DepotUploader _uploader = new();
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotPublicationRow>>> ListAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(() => Task.FromResult<IReadOnlyList<DepotPublicationRow>>([.. settings.Publications
            .Where(link => link.ConnectionId == request.ConnectionId && (request.Project is null || link.Project == request.Project))
            .Select(link => new DepotPublicationRow(link, cache.Get<DepotPublishReport>(_LastKey(link.Id))))]));

    public Task<DepotBrowserAnswer<DepotPublication>> AddAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var project = DepotRequests.Required(request.Project, "project");
            var source = DepotRequests.Required(request.Source, "source");
            await _mcp.RequireEditorAsync(request.ConnectionId, project, "Publishing", cancellationToken).ConfigureAwait(false);
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
        DepotRequests.RunAsync(async () =>
        {
            var link = await _LinkAsync(request, cancellationToken).ConfigureAwait(false);
            settings.Publications = [.. settings.Publications.Where(candidate => candidate.Id != link.Id)];
            _WriteManifest(link.Id, new Dictionary<string, PublishManifestEntry>());
            cache.Set<DepotPublishReport?>(_LastKey(link.Id), null);
            return true;
        });

    public Task<DepotBrowserAnswer<DepotPublishPlan>> PlanAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var link = await _LinkAsync(request, cancellationToken).ConfigureAwait(false);
            var items = (await _BuildAsync(link, cancellationToken).ConfigureAwait(false)).Select(step => step.Item).ToList();
            return new DepotPublishPlan(items, _Fingerprint(items));
        });

    public Task<DepotBrowserAnswer<DepotPublishReport>> RunAsync(DepotPublishRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
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
                return await _ExecuteAsync(link, request.OverwriteConflicts, request.PlanFingerprint, cancellationToken).ConfigureAwait(false);
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
        await _mcp.RequireEditorAsync(link.ConnectionId, link.Project, "Publishing", cancellationToken).ConfigureAwait(false);
        return link;
    }

    private async Task<IReadOnlyList<_Step>> _BuildAsync(DepotPublication link, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(link.Source) && !File.Exists(link.Source))
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "The source does not exist on this machine.");
        }

        var manifest = _ReadManifest(link.Id);
        List<_Local> locals;
        try
        {
            locals = await Task.Run(() => _Scan(link.Source, manifest, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "The source could not be read.");
        }

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
            var untouched = remote is null || remote is not null;
            steps.Add(new _Step(
                new DepotPublishItem(known.Path, known.Kind, untouched ? DepotPublishAction.Delete : DepotPublishAction.DeleteConflict, untouched ? null : "Changed in Depot since the last publication."),
                null,
                remote));
        }

        return steps;
    }

    private async Task<DepotPublishReport> _ExecuteAsync(DepotPublication link, bool overwriteConflicts, string? fingerprint, CancellationToken cancellationToken)
    {
        var steps = await _BuildAsync(link, cancellationToken).ConfigureAwait(false);
        if (fingerprint != _Fingerprint(steps.Select(step => step.Item)))
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "The source or Depot changed since the plan was made. Plan again.");
        }

        var manifest = _ReadManifest(link.Id);
        int written = 0, adopted = 0, deleted = 0, unchanged = 0;
        var problems = new List<DepotPublishItem>();
        try
        {
            var documentWrites = await _WriteDocumentsAsync(
                link,
                steps.Where(step => step.Local?.Kind == DepotBrowserKind.Document && _Writes(step.Item.Action, overwriteConflicts)).ToList(),
                cancellationToken).ConfigureAwait(false);
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = step.Item;
                var local = step.Local;
                try
                {
                    switch (item.Action)
                    {
                        case DepotPublishAction.Unchanged when local?.Checksum is { } checksum:
                            manifest[item.Path] = _Entry(local, checksum);
                            unchanged++;
                            break;
                        case DepotPublishAction.Adopt when local is not null && step.Remote is { } remote:
                            manifest[item.Path] = _Entry(local, remote);
                            adopted++;
                            break;
                        case DepotPublishAction.New or DepotPublishAction.Changed when local is not null:
                            if (item.Kind == DepotBrowserKind.Document)
                            {
                                _ApplyDocumentWrite(step, documentWrites[item.Path], manifest, problems, ref written);
                            }
                            else
                            {
                                manifest[item.Path] = await _WriteArtifactAsync(link, local, step.Remote, cancellationToken).ConfigureAwait(false);
                                written++;
                            }

                            break;
                        case DepotPublishAction.Conflict when overwriteConflicts && local is not null:
                            if (item.Kind == DepotBrowserKind.Document)
                            {
                                _ApplyDocumentWrite(step, documentWrites[item.Path], manifest, problems, ref written);
                            }
                            else
                            {
                                manifest[item.Path] = await _WriteArtifactAsync(link, local, step.Remote, cancellationToken).ConfigureAwait(false);
                                written++;
                            }

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

    private static bool _Writes(string action, bool overwriteConflicts) =>
        action is DepotPublishAction.New or DepotPublishAction.Changed
        || action == DepotPublishAction.Conflict && overwriteConflicts;

    private static void _ApplyDocumentWrite(
        _Step step,
        _DocumentWrite result,
        IDictionary<string, PublishManifestEntry> manifest,
        ICollection<DepotPublishItem> problems,
        ref int written)
    {
        if (result.Entry is not null)
        {
            manifest[step.Item.Path] = result.Entry;
            written++;
            return;
        }

        problems.Add(step.Item with
        {
            Action = result.Action ?? DepotPublishAction.Failed,
            Detail = result.Detail ?? "Depot did not return an outcome for the file.",
        });
    }

    // One sync request may span several MCP-sized bundles; Depot chunks it and answers once per document.
    private async Task<Dictionary<string, _DocumentWrite>> _WriteDocumentsAsync(
        DepotPublication link, IReadOnlyList<_Step> steps, CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            return new Dictionary<string, _DocumentWrite>(StringComparer.Ordinal);
        }

        var entries = new List<object>(steps.Count);
        var documentSteps = new List<(_Step Step, _Local Local, string Checksum)>(steps.Count);
        foreach (var step in steps)
        {
            if (step.Local is not { Checksum: { } localChecksum } local)
            {
                throw new InvalidOperationException("A document write requires a readable local file.");
            }

            var bytes = await File.ReadAllBytesAsync(local.FullPath, cancellationToken).ConfigureAwait(false);
            var content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            entries.Add(new { path = _DepotPath(link, step.Item.Path), content, baseChecksum = step.Remote });
            documentSteps.Add((step, local, localChecksum));
        }

        using var api = _Api(link.ConnectionId);
        using var response = await _PostJsonAsync(
            api, $"{_SyncRoot(link.Project)}/write", entries, _Connection(link.ConnectionId).Name, cancellationToken)
            .ConfigureAwait(false);
        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var result in _Array(response.RootElement, "results"))
        {
            if (DepotRequests.String(result, "path") is { } path)
            {
                results.TryAdd(path, result);
            }
        }

        var writes = new Dictionary<string, _DocumentWrite>(StringComparer.Ordinal);
        foreach (var (step, local, localChecksum) in documentSteps)
        {
            var depotPath = _DepotPath(link, step.Item.Path);
            if (!results.TryGetValue(depotPath, out var result))
            {
                writes[step.Item.Path] = new _DocumentWrite(null, DepotPublishAction.Failed, "Depot returned no outcome for this file.");
                continue;
            }

            var status = DepotRequests.String(result, "status");
            var message = DepotRequests.String(result, "message");
            writes[step.Item.Path] = status switch
            {
                "written" => new _DocumentWrite(
                    _Entry(local, DepotRequests.String(result, "checksum") ?? localChecksum), null, null),
                "conflict" => new _DocumentWrite(null, DepotPublishAction.Conflict, message ?? "Changed in Depot since the plan."),
                _ => new _DocumentWrite(null, DepotPublishAction.Failed, message ?? "Depot did not write the file."),
            };
        }

        return writes;
    }

    // The manifest entry is returned only once Depot has the bytes, so a failed upload leaves the path out of it.
    private async Task<PublishManifestEntry> _WriteArtifactAsync(
        DepotPublication link, _Local local, string? remoteChecksum, CancellationToken cancellationToken)
    {
        if (local.Checksum is not { } localChecksum)
        {
            throw new InvalidOperationException("An artifact write requires a readable local file.");
        }

        var depotPath = _DepotPath(link, local.Relative);
        await using var stream = new FileStream(local.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var contentType = DepotUploadKinds.ContentType(local.Relative);
        var length = stream.Length;
        using var api = _Api(link.ConnectionId);
        var ticket = await _uploader.StartAsync(
            api, _Connection(link.ConnectionId).Name, _SyncRoot(link.Project), depotPath, null, contentType, length, null, cancellationToken)
            .ConfigureAwait(false);
        await _RecheckArtifactAsync(api, link, depotPath, remoteChecksum, cancellationToken).ConfigureAwait(false);
        var checksum = await DepotUploader.PutAsync(
            http, ticket.UploadUrl ?? throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return an upload URL."), stream, contentType, cancellationToken).ConfigureAwait(false);
        return new PublishManifestEntry(local.Relative, local.Kind, checksum ?? localChecksum, length, local.Mtime);
    }

    // request_upload has no baseChecksum, so the plan's checksum is checked once more right before the PUT.
    // ponytail: a race of milliseconds remains, until Depot takes a baseChecksum on the upload.
    private async Task _RecheckArtifactAsync(
        DepotApi api, DepotPublication link, string depotPath, string? expected, CancellationToken cancellationToken)
    {
        var path = $"{_SyncRoot(link.Project)}/artifacts?path={Uri.EscapeDataString(depotPath)}&includeChecksums=true";
        using var listing = await _GetAsync(api, path, _Connection(link.ConnectionId).Name, cancellationToken).ConfigureAwait(false);
        var current = DepotRequests.Entries(listing.RootElement)
            .Where(entry => DepotRequests.String(entry, "path") == depotPath)
            .Select(entry => DepotRequests.String(entry, "checksum") ?? string.Empty)
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

        using var api = _Api(link.ConnectionId);
        var path = $"{_SyncRoot(link.Project)}/delete?path={Uri.EscapeDataString(_DepotPath(link, step.Item.Path))}"
            + $"&kind={(step.Item.Kind == DepotBrowserKind.Document ? "memory" : "artifact")}"
            + $"&baseChecksum={Uri.EscapeDataString(step.Remote)}";
        DepotApiResult result;
        try
        {
            result = await api.PostAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot, or it took too long.");
        }

        return result.Outcome switch
        {
            DepotApiOutcome.Success => true,
            DepotApiOutcome.OlderDepot => false,
            DepotApiOutcome.NotSignedIn => throw new DepotFailure(
                DepotBrowserStatus.SignIn, $"Sign in to {_Connection(link.ConnectionId).Name}"),
            DepotApiOutcome.Conflict => throw new _ConflictException("Changed in Depot since the plan."),
            _ => throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not delete the file."),
        };
    }

    private async Task<(Dictionary<string, string> Documents, Dictionary<string, string> Artifacts)> _RemoteAsync(
        DepotPublication link, CancellationToken cancellationToken)
    {
        var prefix = link.TargetFolder.Length == 0 ? string.Empty : link.TargetFolder + "/";
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cursor = null;
        var complete = false;
        for (var page = 0; page < MaxPages; page++)
        {
            var path = $"{_SyncRoot(link.Project)}/files?includeChecksums=true&limit={PageSize}"
                + (link.TargetFolder.Length == 0 ? string.Empty : $"&path={Uri.EscapeDataString(link.TargetFolder)}")
                + (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
            using var api = _Api(link.ConnectionId);
            using var listing = await _GetAsync(api, path, _Connection(link.ConnectionId).Name, cancellationToken).ConfigureAwait(false);
            _Collect(listing.RootElement, prefix, documents);
            var next = DepotRequests.String(listing.RootElement, "nextCursor");
            if (string.IsNullOrEmpty(next) || next == cursor)
            {
                complete = true;
                break;
            }

            cursor = next;
        }

        if (!complete)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "This Depot folder holds too many documents to compare. Publish to a smaller folder.");
        }

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var artifactPath = $"{_SyncRoot(link.Project)}/artifacts?includeChecksums=true"
            + (link.TargetFolder.Length == 0 ? string.Empty : $"&path={Uri.EscapeDataString(link.TargetFolder)}");
        using var artifactApi = _Api(link.ConnectionId);
        using var artifactListing = await _GetAsync(
            artifactApi, artifactPath, _Connection(link.ConnectionId).Name, cancellationToken).ConfigureAwait(false);
        _Collect(artifactListing.RootElement, prefix, artifacts);
        return (documents, artifacts);
    }

    private DepotApi _Api(string connectionId) => new(_host, _Connection(connectionId));

    private DepotConnectionRegistration _Connection(string connectionId) =>
        _connections().FirstOrDefault(candidate => candidate.Id == connectionId)
        ?? throw new DepotFailure(DepotBrowserStatus.Error, "That Depot connection is no longer configured.");

    private static string _SyncRoot(string project)
    {
        var address = project.Split('/', 2);
        return address.Length == 2 && address.All(part => !string.IsNullOrWhiteSpace(part))
            ? $"sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}"
            : throw new DepotFailure(DepotBrowserStatus.Error, $"Depot project '{project}' has no organization-qualified address.");
    }

    private static Task<JsonDocument> _GetAsync(
        DepotApi api, string path, string connectionName, CancellationToken cancellationToken) =>
        _DocumentAsync(api.GetAsync(path, cancellationToken), connectionName, cancellationToken);

    private static Task<JsonDocument> _PostJsonAsync<T>(
        DepotApi api, string path, T value, string connectionName, CancellationToken cancellationToken) =>
        _DocumentAsync(api.PostJsonAsync(path, value, cancellationToken), connectionName, cancellationToken);

    private static async Task<JsonDocument> _DocumentAsync(
        Task<DepotApiResult> call, string connectionName, CancellationToken cancellationToken)
    {
        DepotApiResult result;
        try
        {
            result = await call.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot, or it took too long.");
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            throw new DepotFailure(DepotBrowserStatus.SignIn, $"Sign in to {connectionName}");
        }

        if (result.Outcome != DepotApiOutcome.Success)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not accept the file operation.");
        }

        try
        {
            return JsonDocument.Parse(result.Content);
        }
        catch (JsonException)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot's answer came back in an unexpected shape.");
        }
    }

    private static IEnumerable<JsonElement> _Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];

    // A listing without a checksum still counts as present, with one nothing equals: it can only become a conflict.
    private static void _Collect(System.Text.Json.JsonElement root, string prefix, Dictionary<string, string> into)
    {
        foreach (var entry in DepotRequests.Entries(root))
        {
            if (DepotRequests.String(entry, "path") is { } path && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                into[path] = DepotRequests.String(entry, "checksum") ?? string.Empty;
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

    private static string _Fingerprint(IEnumerable<DepotPublishItem> items) =>
        _Sha(Encoding.UTF8.GetBytes(string.Join(';', items.Select(item => $"{item.Action}|{item.Kind}|{item.Path}"))));

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
