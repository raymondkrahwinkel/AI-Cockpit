using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class ConversationIndexStore : IConversationIndex, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly ILogger<ConversationIndexStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ConversationIndexStore(ILogger<ConversationIndexStore> logger) : this(CockpitConfigPath.Conversations, logger) { }
    internal ConversationIndexStore(string path, ILogger<ConversationIndexStore> logger) => (_path, _logger) = (path, logger);

    public async Task RecordAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.PaneId == record.PaneId);
            record = record with
            {
                StartedAt = existing?.StartedAt ?? record.StartedAt,
                ProviderId = record.ProviderId ?? existing?.ProviderId,
                ConversationId = record.ConversationId ?? existing?.ConversationId,
                ConversationState = record.ConversationState == SessionConversationIdState.Unknown && existing is not null
                    ? existing.ConversationState : record.ConversationState,
                TranscriptPath = record.TranscriptPath ?? existing?.TranscriptPath,
            };
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) CockpitConfigPath.EnsurePrivateDirectory(directory);
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, SerializerOptions).AsMemory(), cancellationToken);
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not append conversation index at {Path}.", _path); }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<ConversationRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var latest = new Dictionary<string, ConversationRecord>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return [];
        try
        {
            using var reader = new StreamReader(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                try { if (JsonSerializer.Deserialize<ConversationRecord>(line, SerializerOptions) is { PaneId.Length: > 0 } record) latest[record.PaneId] = record; }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not read conversation index at {Path}.", _path); }
        return [.. latest.Values.OrderByDescending(record => record.LastActivityAt)];
    }

    public async Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(string text, string? projectId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var needle = text.Trim();
        var records = (await LoadAsync(cancellationToken)).Where(record =>
            (projectId is null || string.Equals(record.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) &&
            (from is null || record.LastActivityAt >= from) && (to is null || record.LastActivityAt <= to));
        var results = new List<ConversationSearchResult>();
        foreach (var record in records)
        {
            var source = $"{record.Title}\n{record.ProjectId}";
            var hit = source.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (hit < 0 && record.TranscriptPath is { } path && File.Exists(path))
            {
                try { source = await File.ReadAllTextAsync(path, cancellationToken); hit = source.IndexOf(needle, StringComparison.OrdinalIgnoreCase); }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not search transcript at {Path}.", path); }
            }
            if (hit >= 0) results.Add(new(record, source[Math.Max(0, hit - 80)..Math.Min(source.Length, hit + needle.Length + 80)]));
        }
        // ponytail: scan transcripts linearly until measured search latency warrants an FTS index.
        return results;
    }
}
