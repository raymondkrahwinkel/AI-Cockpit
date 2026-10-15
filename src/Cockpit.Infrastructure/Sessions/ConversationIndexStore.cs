using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

// AC-1531: one row per conversation that outlives its pane — `conversations.jsonl`, append-only, the last row per pane
// wins, and nothing compacts it. The pane supplies what it shows (title, project, status, closed); profile, provider,
// place and conversation id are the session-state recorder's facts, so the index takes them from there.
internal sealed class ConversationIndexStore : IConversationIndex, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly IProjectStore? _projects;
    private readonly SessionTranscriptLog? _transcripts;
    private readonly ILogger<ConversationIndexStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, SessionStateRecord> _sessionFacts = new(StringComparer.Ordinal);
    private Dictionary<string, ConversationRecord>? _latest;

    public ConversationIndexStore(SessionStateRecorder recorder, IProjectStore projects, SessionTranscriptLog transcripts, ILogger<ConversationIndexStore> logger)
        : this(CockpitConfigPath.Conversations, logger, projects, transcripts) => recorder.Recorded += RecordSessionState;

    internal ConversationIndexStore(string path, ILogger<ConversationIndexStore> logger, IProjectStore? projects = null, SessionTranscriptLog? transcripts = null) =>
        (_path, _logger, _projects, _transcripts) = (path, logger, projects, transcripts);

    public Task RecordAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
        _WriteAsync(record.PaneId, existing => _WithSessionFacts(record with
        {
            StartedAt = existing?.StartedAt ?? record.StartedAt,
            ProfileId = record.ProfileId ?? existing?.ProfileId,
            ProviderId = record.ProviderId ?? existing?.ProviderId,
            WorkingDirectory = record.WorkingDirectory ?? existing?.WorkingDirectory,
            ConversationId = record.ConversationId ?? existing?.ConversationId,
            ConversationState = record.ConversationState == SessionConversationIdState.Unknown && existing is not null
                ? existing.ConversationState
                : record.ConversationState,
        }), cancellationToken);

    // A pane the index has no row for yet only gets one from the pane itself; the assistant has no pane that would.
    internal void RecordSessionState(SessionStateRecord state)
    {
        _sessionFacts[state.PaneId] = state;
        _ = _WriteAsync(state.PaneId, existing => existing is null && state.PaneId != AssistantIdentity.PaneId
            ? null
            : _WithSessionFacts(existing ?? _AssistantRow(state)), CancellationToken.None);
    }

    public async Task<IReadOnlyList<ConversationRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. (await _LatestAsync(cancellationToken).ConfigureAwait(false)).Values.OrderByDescending(record => record.LastActivityAt)];
        }
        finally
        {
            _gate.Release();
        }
    }

    // ponytail: every matching transcript is read per search, linear in their total size; an FTS index once that is slow.
    public async Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(string text, string? projectId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var needle = text.Trim();
        var projectNames = _projects is null
            ? new Dictionary<string, string>()
            : (await _projects.LoadAsync(cancellationToken).ConfigureAwait(false)).Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var results = new List<ConversationSearchResult>();
        foreach (var record in await LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((projectId is not null && record.ProjectId != projectId) || record.LastActivityAt < from || record.LastActivityAt > to)
            {
                continue;
            }

            var fragment = _Fragment(record.Title, needle)
                ?? _Fragment(record.ProjectId is { } id ? projectNames.GetValueOrDefault(id) : null, needle)
                ?? await _TranscriptFragmentAsync(record.TranscriptPath, needle, cancellationToken).ConfigureAwait(false);
            if (fragment is not null)
            {
                results.Add(new ConversationSearchResult(record, fragment));
            }
        }

        return results;
    }

    private async Task _WriteAsync(string paneId, Func<ConversationRecord?, ConversationRecord?> compose, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = await _LatestAsync(cancellationToken).ConfigureAwait(false);
            if (compose(latest.GetValueOrDefault(paneId)) is not { } record)
            {
                return;
            }

            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
            {
                CockpitConfigPath.EnsurePrivateDirectory(directory);
            }

            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(_path, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine), cancellationToken).ConfigureAwait(false);
            }

            latest[paneId] = record;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not append to the conversation index at {Path}.", _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Read once and then kept, so a write is an append rather than a reread; a failed read is not kept, so the next call retries.
    private async Task<Dictionary<string, ConversationRecord>> _LatestAsync(CancellationToken cancellationToken)
    {
        if (_latest is not null)
        {
            return _latest;
        }

        var latest = new Dictionary<string, ConversationRecord>(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return _latest = latest;
        }

        try
        {
            foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ConversationRecord>(line, SerializerOptions) is { PaneId.Length: > 0 } record)
                    {
                        latest[record.PaneId] = record;
                    }
                }
                catch (JsonException)
                {
                }
            }

            return _latest = latest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not read the conversation index at {Path}.", _path);
            return latest;
        }
    }

    private ConversationRecord _WithSessionFacts(ConversationRecord record) =>
        _sessionFacts.TryGetValue(record.PaneId, out var state)
            ? record with
            {
                ProfileId = state.ProfileId ?? record.ProfileId,
                ProviderId = state.ProviderId ?? record.ProviderId,
                WorkingDirectory = state.WorkingDirectory ?? record.WorkingDirectory,
                ConversationId = state.ConversationId,
                ConversationState = state.ConversationState,
            }
            : record;

    private ConversationRecord _AssistantRow(SessionStateRecord state) => new(
        state.PaneId, AssistantProfileSlot.DisplayName, null, null, null, null, null, SessionConversationIdState.Unknown,
        state.RecordedAt, state.RecordedAt, null, null, false, true, _transcripts?.LogPath(state.PaneId));

    private static string? _Fragment(string? source, string needle)
    {
        if (source is null || source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) is not (>= 0 and var hit))
        {
            return null;
        }

        return source[Math.Max(0, hit - 80)..Math.Min(source.Length, hit + needle.Length + 80)];
    }

    private async Task<string?> _TranscriptFragmentAsync(string? path, string needle, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (_Fragment(_SaidOn(line), needle) is { } fragment)
                {
                    return fragment;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not search the transcript at {Path}.", path);
            return null;
        }
    }

    // AC-1532: what a transcript row says, so the fragment the sheet shows is words and not the row's JSON.
    private static string? _SaidOn(string line)
    {
        try
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.ValueKind == JsonValueKind.Object
                && row.RootElement.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
