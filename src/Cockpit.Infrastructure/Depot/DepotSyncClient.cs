using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Depot;

// Talks to Depot's memory-tree content contract through the same `IMcpToolInvoker` seam Cockpit.Plugin.Depot's
// own `CockpitProjectDefinitionStore` already uses for `.cockpit/project.json` (AC-243/244) — no new transport,
// since AC-280 found no established raw REST surface next to the MCP contract.
internal sealed class DepotSyncClient : IDepotSyncClient, ISingletonService
{
    // `write_many`'s cap ("Capped by Depot:Brain") is a server-side config value this client cannot read, and
    // AC-280 forbids assuming a number — this is a conservative round size, not a measured limit. Lower it if a
    // real cap is ever confirmed smaller.
    private const int _BatchSize = 25;

    // A page size for `list`, not a cap Depot enforces — `includeChecksums: true` costs Depot a per-file read, so
    // keeping pages modest avoids one very large round on a big memory tree.
    private const int _ListPageSize = 200;

    private const int _MaxAttempts = 3;
    private static readonly TimeSpan _RetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly IMcpToolInvoker _invoker;
    private readonly TimeSpan _retryDelay;

    public DepotSyncClient(IMcpToolInvoker invoker) : this(invoker, _RetryDelay)
    {
    }

    // Test seam: skip the real delay between retries.
    internal DepotSyncClient(IMcpToolInvoker invoker, TimeSpan retryDelay)
    {
        _invoker = invoker;
        _retryDelay = retryDelay;
    }

    public async Task<DepotListResult> ListAllAsync(
        string serverName, string project, string? path = null, CancellationToken cancellationToken = default)
    {
        var files = new List<DepotFileEntry>();
        string? cursor = null;

        do
        {
            var arguments = new Dictionary<string, object?>
            {
                ["project"] = project,
                ["path"] = path,
                ["includeChecksums"] = true,
                ["limit"] = _ListPageSize,
                ["after"] = cursor,
            };

            var result = await _InvokeWithRetryAsync(serverName, "list", arguments, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case McpToolInvocationOutcome.AuthorizationRequired:
                    return DepotListResult.AuthorizationRequired;
                case McpToolInvocationOutcome.Failed:
                    // A page failing mid-listing must not leave the caller believing the pages already collected
                    // are the whole tree — the whole call fails rather than returning a silently truncated list.
                    return DepotListResult.Failed(result.Error ?? "Depot did not return a file listing.");
            }

            _ListEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<_ListEnvelope>(result.Content ?? string.Empty, _SerializerOptions);
            }
            catch (JsonException exception)
            {
                return DepotListResult.Failed($"Couldn't read Depot's listing: {exception.Message}");
            }

            if (envelope?.Files is not { } page)
            {
                return DepotListResult.Failed("Depot's listing came back in an unexpected shape.");
            }

            files.AddRange(page
                .Where(entry => entry.Path is { Length: > 0 })
                .Select(entry => new DepotFileEntry(entry.Path!, entry.Size, entry.UpdatedAt, entry.Checksum)));
            cursor = envelope.NextCursor;
        }
        while (cursor is { Length: > 0 });

        return DepotListResult.Success(files);
    }

    public async Task<DepotReadManyResult> ReadManyAsync(
        string serverName, string project, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var files = new List<DepotReadFile>();
        var missing = new List<string>();
        var unreadable = new List<string>();

        foreach (var round in _Chunk(paths, _BatchSize))
        {
            var arguments = new Dictionary<string, object?>
            {
                ["project"] = project,
                ["paths"] = round,
                ["includeChecksums"] = true,
            };

            var result = await _InvokeWithRetryAsync(serverName, "read_many", arguments, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == McpToolInvocationOutcome.AuthorizationRequired)
            {
                return DepotReadManyResult.AuthorizationRequired;
            }

            if (result.Outcome == McpToolInvocationOutcome.Failed)
            {
                // This round never got an answer — every path it asked about is unreadable, not silently missing.
                unreadable.AddRange(round);
                continue;
            }

            _ReadManyEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<_ReadManyEnvelope>(result.Content ?? string.Empty, _SerializerOptions);
            }
            catch (JsonException)
            {
                unreadable.AddRange(round);
                continue;
            }

            if (envelope is null)
            {
                unreadable.AddRange(round);
                continue;
            }

            files.AddRange((envelope.Files ?? [])
                .Where(entry => entry.Path is { Length: > 0 } && entry.Content is not null)
                .Select(entry => new DepotReadFile(entry.Path!, entry.Content!, entry.Checksum)));
            missing.AddRange(envelope.Missing ?? []);
        }

        return DepotReadManyResult.Success(files, missing, unreadable);
    }

    public async Task<DepotWriteManyResult> WriteManyAsync(
        string serverName, string project, IReadOnlyList<DepotWriteEntry> entries, CancellationToken cancellationToken = default)
    {
        var results = new List<DepotWriteEntryResult>();

        foreach (var round in _Chunk(entries, _BatchSize))
        {
            var arguments = new Dictionary<string, object?>
            {
                ["project"] = project,
                ["entries"] = round
                    .Select(entry => new Dictionary<string, object?>
                    {
                        ["path"] = entry.Path,
                        ["content"] = entry.Content,
                        ["baseChecksum"] = entry.BaseChecksum,
                    })
                    .ToList(),
            };

            var result = await _InvokeWithRetryAsync(serverName, "write_many", arguments, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == McpToolInvocationOutcome.AuthorizationRequired)
            {
                results.AddRange(round.Select(entry =>
                    new DepotWriteEntryResult(entry.Path, DepotWriteStatus.Failed, null, "Sign in to this Depot connection to write here.")));
                continue;
            }

            if (result.Outcome == McpToolInvocationOutcome.Failed)
            {
                // Criterion 4: an over-cap or unreachable round must not read as a silent success for any file
                // it was supposed to write — every path in this round gets an explicit Failed result instead.
                results.AddRange(round.Select(entry =>
                    new DepotWriteEntryResult(entry.Path, DepotWriteStatus.Failed, null, result.Error ?? "Depot did not confirm this write.")));
                continue;
            }

            _WriteManyEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<_WriteManyEnvelope>(result.Content ?? string.Empty, _SerializerOptions);
            }
            catch (JsonException exception)
            {
                results.AddRange(round.Select(entry =>
                    new DepotWriteEntryResult(entry.Path, DepotWriteStatus.Failed, null, $"Couldn't read Depot's response: {exception.Message}")));
                continue;
            }

            var byPath = (envelope?.Results ?? [])
                .Where(entry => entry.Path is { Length: > 0 })
                .ToDictionary(entry => entry.Path!, StringComparer.Ordinal);

            foreach (var entry in round)
            {
                results.Add(byPath.TryGetValue(entry.Path, out var reported)
                    ? new DepotWriteEntryResult(entry.Path, _ParseWriteStatus(reported.Status), reported.Checksum, reported.Message)
                    : new DepotWriteEntryResult(entry.Path, DepotWriteStatus.Failed, null, "Depot's response did not cover this file."));
            }
        }

        return new DepotWriteManyResult(results);
    }

    public async Task<DepotMutationResult> MoveAsync(
        string serverName, string project, string from, string to, string? baseChecksum,
        bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["project"] = project,
            ["from"] = from,
            ["to"] = to,
            ["overwrite"] = overwrite,
            ["baseChecksum"] = baseChecksum,
        };

        var result = await _InvokeWithRetryAsync(serverName, "move", arguments, cancellationToken).ConfigureAwait(false);
        return _ToMutationResult(result);
    }

    public async Task<DepotMutationResult> DeleteAsync(
        string serverName, string project, string path, string? baseChecksum,
        bool hard = false, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["project"] = project,
            ["path"] = path,
            ["baseChecksum"] = baseChecksum,
            ["hard"] = hard,
        };

        var result = await _InvokeWithRetryAsync(serverName, "delete", arguments, cancellationToken).ConfigureAwait(false);
        return _ToMutationResult(result);
    }

    public async Task<DepotListVersionsResult> ListVersionsAsync(
        string serverName, string project, string path, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["project"] = project, ["path"] = path };

        var result = await _InvokeWithRetryAsync(serverName, "list_versions", arguments, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case McpToolInvocationOutcome.AuthorizationRequired:
                return DepotListVersionsResult.AuthorizationRequired;
            case McpToolInvocationOutcome.Failed:
                return DepotListVersionsResult.Failed(result.Error ?? "Depot did not return this file's version history.");
        }

        _ListVersionsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<_ListVersionsEnvelope>(result.Content ?? string.Empty, _SerializerOptions);
        }
        catch (JsonException exception)
        {
            return DepotListVersionsResult.Failed($"Couldn't read Depot's version history: {exception.Message}");
        }

        if (envelope?.Versions is not { } versions)
        {
            return DepotListVersionsResult.Failed("Depot's version history came back in an unexpected shape.");
        }

        return DepotListVersionsResult.Success(versions
            .Where(entry => entry.VersionId is { Length: > 0 })
            .Select(entry => new DepotFileVersion(entry.VersionId!, entry.CreatedAt, entry.Size, entry.Checksum))
            .ToList());
    }

    private static DepotMutationResult _ToMutationResult(McpToolInvocationResult result) => result.Outcome switch
    {
        McpToolInvocationOutcome.Success => DepotMutationResult.Success,
        McpToolInvocationOutcome.AuthorizationRequired => DepotMutationResult.AuthorizationRequired,
        _ => _IsChecksumConflict(result.Error)
            ? DepotMutationResult.Conflict(result.Error!)
            : DepotMutationResult.Failed(result.Error ?? "Depot did not confirm this change."),
    };

    // The same wire text `CockpitProjectDefinitionWriteResult` classifies for `write` (measured live, AC-247) —
    // `move`/`delete` document the identical conflict contract, so this assumes the same phrasing and falls
    // back to Failed rather than guessing if Depot's wording ever differs here.
    private static bool _IsChecksumConflict(string? error) =>
        error is not null && error.Contains("changed since it was read; current checksum is", StringComparison.Ordinal);

    private static DepotWriteStatus _ParseWriteStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "written" => DepotWriteStatus.Written,
        "conflict" => DepotWriteStatus.Conflict,
        "invalid" => DepotWriteStatus.Invalid,
        _ => DepotWriteStatus.Failed,
    };

    // Retries only a round that failed outright (unreachable Depot, a transient error) — never
    // AuthorizationRequired, which a retry cannot fix. An unreachable Depot is a normal, expected state
    // (AC-280's own framing), not something to surface after a single blip.
    private async Task<McpToolInvocationResult> _InvokeWithRetryAsync(
        string serverName, string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        McpToolInvocationResult result;
        for (var attempt = 1; ; attempt++)
        {
            result = await _invoker.InvokeAsync(serverName, toolName, arguments, projectId: null, callerFallbackServers: null, cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome != McpToolInvocationOutcome.Failed || attempt >= _MaxAttempts)
            {
                return result;
            }

            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<IReadOnlyList<T>> _Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var offset = 0; offset < items.Count; offset += size)
        {
            yield return [.. items.Skip(offset).Take(size)];
        }
    }

    private static readonly JsonSerializerOptions _SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed class _ListEnvelope
    {
        public List<_ListFileEntry>? Files { get; set; }
        public string? NextCursor { get; set; }
    }

    private sealed class _ListFileEntry
    {
        public string? Path { get; set; }
        public long Size { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Checksum { get; set; }
    }

    private sealed class _ReadManyEnvelope
    {
        public List<_ReadManyFileEntry>? Files { get; set; }
        public List<string>? Missing { get; set; }
    }

    private sealed class _ReadManyFileEntry
    {
        public string? Path { get; set; }
        public string? Content { get; set; }
        public string? Checksum { get; set; }
    }

    private sealed class _WriteManyEnvelope
    {
        public List<_WriteManyResultEntry>? Results { get; set; }
    }

    private sealed class _WriteManyResultEntry
    {
        public string? Path { get; set; }
        public string? Status { get; set; }
        public string? Checksum { get; set; }
        public string? Message { get; set; }
    }

    private sealed class _ListVersionsEnvelope
    {
        public List<_ListVersionsEntry>? Versions { get; set; }
    }

    private sealed class _ListVersionsEntry
    {
        public string? VersionId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long Size { get; set; }
        public string? Checksum { get; set; }
    }
}
