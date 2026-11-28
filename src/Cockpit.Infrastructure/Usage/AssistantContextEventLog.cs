using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Usage;

// AC-2419: one line per compaction or fresh restart of the assistant, to set its compaction threshold on figures.
public sealed class AssistantContextEventLog : ISingletonService
{
    internal const int MaxLines = 10_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AssistantContextEventLog(ILogger<AssistantContextEventLog> logger)
        : this(AuditTrailFiles.InStateRoot(AuditTrailFiles.AssistantContextEvents), logger)
    {
    }

    internal AssistantContextEventLog(string filePath, ILogger<AssistantContextEventLog> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    // Never throws: a failed measurement must not stop the reset.
    internal async Task RecordAsync(AssistantContextEvent entry)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var lines = File.Exists(_filePath) ? await File.ReadAllLinesAsync(_filePath).ConfigureAwait(false) : [];
            string[] kept = [.. lines.TakeLast(MaxLines - 1), JsonSerializer.Serialize(entry, SerializerOptions)];
            CockpitConfigPath.ReplaceAtomicallyPrivate(_filePath, string.Join(Environment.NewLine, kept) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not write the assistant context event log at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

// Figures as they stood at the trigger; null is "not counted", never zero.
internal sealed record AssistantContextEvent(
    DateTimeOffset At,
    string Kind,
    string Reason,
    double? FillPercent,
    long? ContextTokens,
    long? CompactInput,
    long? CompactOutput,
    int? NoteStateAgeMinutes)
{
    public const string Compact = "compact";
    public const string Restart = "restart";
    public const string Threshold = "threshold";
    public const string CompactFailed = "compact-failed";
    public const string NoCompactionSupport = "no-compaction-support";
    public const string CompactionLimit = "compaction-limit";
    public const string CompactionIneffective = "compaction-ineffective";

    // The same core as the line, for the divider in the assistant's transcript.
    public string Describe(int compactions = 0)
    {
        var fill = FillPercent is { } percent ? FormattableString.Invariant($" at {percent:0}%") : string.Empty;
        var note = NoteStateAgeMinutes is { } minutes ? $"state note {minutes} min old" : "no state note";
        if (Kind == Compact)
        {
            return $"Context compacted{fill} — {note}";
        }

        var lead = Reason switch
        {
            CompactionLimit => $"Fresh start after {compactions} compaction{(compactions == 1 ? "" : "s")}{fill}",
            CompactionIneffective => $"Fresh start, compacting left the context{fill}",
            _ => $"Context restarted{fill}",
        };
        return $"{lead} — {note}; a new conversation starts here";
    }
}
