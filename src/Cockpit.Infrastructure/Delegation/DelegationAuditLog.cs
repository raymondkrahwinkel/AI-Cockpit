using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Appends the delegation audit trail (#67) to <c>delegation-audit.jsonl</c> next to <c>cockpit.json</c> — one
/// JSON object per line, so it survives a restart, can be tailed while the app runs, and stays readable with a
/// text editor. Kept out of <c>cockpit.json</c> deliberately: settings are rewritten wholesale on every save, and
/// an append-only trail must not be something a settings write can truncate.
/// </summary>
internal sealed class DelegationAuditLog : IDelegationAuditLog, ISingletonService
{
    /// <summary>Prompts are trimmed: the log is for recognising a task later, not for keeping a copy of every transcript.</summary>
    private const int MaxPromptLength = 300;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _logFilePath;
    private readonly ILogger<DelegationAuditLog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DelegationAuditLog(ILogger<DelegationAuditLog> logger)
        : this(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
    internal DelegationAuditLog(string logFilePath, ILogger<DelegationAuditLog> logger)
    {
        _logFilePath = logFilePath;
        _logger = logger;
    }

    public async Task RecordAsync(DelegationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(entry with { Prompt = _Trim(entry.Prompt) }, SerializerOptions);
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A broken audit log must not take a delegation down with it — losing the record is bad, losing the
            // work is worse. It is logged rather than swallowed, so a silently unwritable log still surfaces.
            _logger.LogWarning(ex, "Could not append to the delegation audit log at {Path}.", _logFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DelegationAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_logFilePath))
        {
            return [];
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(_logFilePath, cancellationToken).ConfigureAwait(false);

            return lines
                .Reverse()
                .Select(_TryParse)
                .Where(entry => entry is not null)
                .Take(limit)
                .Select(entry => entry!)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the delegation audit log at {Path}.", _logFilePath);
            return [];
        }
    }

    // A half-written or hand-edited line is skipped rather than throwing away the whole trail.
    private static DelegationAuditEntry? _TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<DelegationAuditEntry>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? _Trim(string? prompt) => prompt is { Length: > MaxPromptLength }
        ? prompt[..MaxPromptLength] + "…"
        : prompt;

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "delegation-audit.jsonl");
}
