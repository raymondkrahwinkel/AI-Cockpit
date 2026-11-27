using System.Collections.Concurrent;
using System.Diagnostics;
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

// The last valid row per pane is authoritative. The pane supplies what it shows, and the session-state
// recorder supplies the profile, provider, working directory and conversation ID.
internal sealed class ConversationIndexStore : IConversationIndex, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan CloseRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseGateWait = TimeSpan.FromSeconds(30);
    private const int CompactionInterval = 128;
    private readonly string _path;
    private readonly IProjectStore? _projects;
    private readonly SessionTranscriptLog? _transcripts;
    private readonly ILogger<ConversationIndexStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _closeDrainLock = new();
    private Task? _closeDrain;
    private bool _closeDrainRequested;
    private readonly string _pendingCloses;
    private readonly ConcurrentDictionary<string, SessionStateRecord> _sessionFacts = new(StringComparer.Ordinal);
    private Dictionary<string, ConversationRecord>? _latest;
    private long _lineCount;

    public ConversationIndexStore(SessionStateRecorder recorder, IProjectStore projects, SessionTranscriptLog transcripts, ILogger<ConversationIndexStore> logger)
        : this(CockpitConfigPath.Conversations, logger, projects, transcripts) => recorder.Recorded += RecordSessionState;

    internal ConversationIndexStore(string path, ILogger<ConversationIndexStore> logger, IProjectStore? projects = null, SessionTranscriptLog? transcripts = null)
    {
        (_path, _logger, _projects, _transcripts) = (path, logger, projects, transcripts);
        _pendingCloses = path + ".pending-closes";
        if (Directory.Exists(_pendingCloses))
        {
            _RequestCloseDrain();
        }
    }

    public Task RecordAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
        _WriteAsync(record.PaneId, existing => _Merge(record, existing), cancellationToken);

    private ConversationRecord? _Merge(ConversationRecord record, ConversationRecord? existing, bool isStagedClose = false)
    {
        if (record.ClosedAt is { } incomingClose)
        {
            if (existing?.ClosedAt >= incomingClose || (isStagedClose && existing?.LastActivityAt > incomingClose))
            {
                return null;
            }
        }
        else if (existing?.ClosedAt is { } existingClose && record.LastActivityAt <= existingClose)
        {
            return null;
        }

        return _WithSessionFacts(record with
        {
            StartedAt = existing?.StartedAt ?? record.StartedAt,
            ProfileId = record.ProfileId ?? existing?.ProfileId,
            ProviderId = record.ProviderId ?? existing?.ProviderId,
            WorkingDirectory = record.WorkingDirectory ?? existing?.WorkingDirectory,
            ConversationId = record.ConversationId ?? existing?.ConversationId,
            ConversationState = record.ConversationState == SessionConversationIdState.Unknown && existing is not null
                ? existing.ConversationState
                : record.ConversationState,
        });
    }

    // Keep the close independently of the index gate and of the next process's shutdown budget.
    public async Task StageCloseAsync(ConversationRecord record)
    {
        if (record.ClosedAt is not { } closedAt)
        {
            throw new ArgumentException("A staged close needs a close time.", nameof(record));
        }

        string destination = Path.Combine(_pendingCloses, FormattableString.Invariant($"{closedAt.UtcTicks:D19}-{Guid.NewGuid():N}.json"));
        string temporary = destination + ".tmp";
        while (true)
        {
            try
            {
                CockpitConfigPath.EnsurePrivateDirectory(_pendingCloses);
                await using (var stream = new FileStream(temporary, _WriteOptions(FileMode.Create)))
                {
                    await JsonSerializer.SerializeAsync(stream, _WithSessionFacts(record), SerializerOptions).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, destination);
                _RequestCloseDrain();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not stage conversation close for pane {PaneId} ({ErrorType}); retrying.", record.PaneId, exception.GetType().Name);
                await Task.Delay(CloseRetryDelay).ConfigureAwait(false);
            }
        }
    }

    private void _RequestCloseDrain()
    {
        lock (_closeDrainLock)
        {
            _closeDrainRequested = true;
            _closeDrain ??= Task.Run(_DrainClosesAsync);
        }
    }

    private async Task _DrainClosesAsync()
    {
        while (true)
        {
            lock (_closeDrainLock)
            {
                _closeDrainRequested = false;
            }

            try
            {
                foreach (string path in Directory.EnumerateFiles(_pendingCloses, "*.json").Order(StringComparer.Ordinal))
                {
                    string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    ConversationRecord record = JsonSerializer.Deserialize<ConversationRecord>(json, SerializerOptions)
                        ?? throw new JsonException("Missing staged close record.");
                    await _WriteAsync(record.PaneId, existing => _Merge(record, existing, isStagedClose: true),
                        CancellationToken.None, isStagedClose: true).ConfigureAwait(false);
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or TimeoutException)
            {
                _logger.LogWarning("Could not drain staged conversation closes ({ErrorType}); retrying.", exception.GetType().Name);
                await Task.Delay(CloseRetryDelay).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Staged conversation close drain stopped ({ErrorType}). Pending closes are retained.", exception.GetType().Name);
            }

            lock (_closeDrainLock)
            {
                if (!_closeDrainRequested)
                {
                    _closeDrain = null;
                    return;
                }
            }
        }
    }

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

    private async Task _WriteAsync(string paneId, Func<ConversationRecord?, ConversationRecord?> compose, CancellationToken cancellationToken, bool isStagedClose = false)
    {
        // AC-2390: the UI thread records conversations; off it before the first lock, ACL and file write (a 5 s freeze, 27-11).
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        long started = Stopwatch.GetTimestamp();
        if (isStagedClose)
        {
            if (!await _gate.WaitAsync(CloseGateWait, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Conversation index gate still busy for pane {PaneId} after {ElapsedMs} ms; close remains staged.",
                    paneId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw new TimeoutException("The conversation index is busy.");
            }
        }
        else
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (isStagedClose)
            {
                _logger.LogInformation("Conversation index gate acquired for pane {PaneId} after {ElapsedMs} ms.", paneId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var latest = await _LatestAsync(cancellationToken).ConfigureAwait(false);
            if (isStagedClose && _latest is null)
            {
                throw new IOException("The conversation index could not be loaded.");
            }
            ConversationRecord? existing = latest.GetValueOrDefault(paneId);
            if (compose(existing) is not { } record || _IsSameRecord(record, existing))
            {
                return;
            }

            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
            {
                CockpitConfigPath.EnsurePrivateDirectory(directory);
            }

            try
            {
                await _AppendAsync(record, isStagedClose, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed append may have reached the file. Reload before another merge or compaction.
                _latest = null;
                throw;
            }

            latest[paneId] = record;
            if (_latest is not null && ++_lineCount % CompactionInterval == 0)
            {
                _CompactIfNeeded(latest);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Could not write conversation index for pane {PaneId} ({ErrorType}).", paneId, exception.GetType().Name);
            if (isStagedClose)
            {
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // DateTimeOffset equality alone ignores offsets, which the index persists.
    private static bool _IsSameRecord(ConversationRecord record, ConversationRecord? existing) =>
        existing is not null && record == existing
        && record.StartedAt.Offset == existing.StartedAt.Offset
        && record.LastActivityAt.Offset == existing.LastActivityAt.Offset
        && record.ClosedAt?.Offset == existing.ClosedAt?.Offset;

    private async Task _AppendAsync(ConversationRecord record, bool isStagedClose, CancellationToken cancellationToken)
    {
        FileStreamOptions options = _WriteOptions(FileMode.OpenOrCreate);
        options.Access = FileAccess.ReadWrite;
        await using var stream = new FileStream(_path, options);
        // A prior interrupted append may have left a partial row. Keep the next record separate.
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine), cancellationToken).ConfigureAwait(false);
        if (isStagedClose)
        {
            stream.Flush(flushToDisk: true);
        }
    }

    private static FileStreamOptions _WriteOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
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

        long lineCount = 0;
        try
        {
            await foreach (string line in File.ReadLinesAsync(_path, cancellationToken).ConfigureAwait(false))
            {
                lineCount++;
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read the conversation index ({ErrorType}).", exception.GetType().Name);
            return [];
        }

        _lineCount = lineCount;
        _CompactIfNeeded(latest);
        return _latest = latest;
    }

    // Under _gate, compact only when at least half the rows can go and the savings justify a rewrite.
    private void _CompactIfNeeded(Dictionary<string, ConversationRecord> latest)
    {
        if (latest.Count == 0 || _lineCount - latest.Count < Math.Max(CompactionInterval, latest.Count))
        {
            return;
        }

        string temporary = $"{_path}.{Guid.NewGuid():N}.new";
        string backupTemporary = temporary + ".bak";
        try
        {
            using (FileStream stream = CockpitConfigPath.CreatePrivateFile(temporary))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    // Keep the first-seen pane order for ties in activity time.
                    foreach (ConversationRecord record in latest.Values)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
                    }
                }

                stream.Flush(flushToDisk: true);
            }

            // Publish the backup first so a failed replacement leaves the original at its path.
            using (FileStream original = File.OpenRead(_path))
            using (FileStream backup = CockpitConfigPath.CreatePrivateFile(backupTemporary))
            {
                original.CopyTo(backup);
                backup.Flush(flushToDisk: true);
            }

            File.Move(backupTemporary, _path + ".bak", overwrite: true);
            File.Move(temporary, _path, overwrite: true);
            _lineCount = latest.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not compact the conversation index ({ErrorType}).", exception.GetType().Name);
            foreach (string sidecar in new[] { temporary, backupTemporary })
            {
                try
                {
                    File.Delete(sidecar);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Could not remove a conversation index temporary file ({ErrorType}).", cleanupError.GetType().Name);
                }
            }
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
                Role = state.Role,
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
