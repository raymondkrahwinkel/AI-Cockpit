using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// Appends the consent audit trail (#AC-47) to <c>consent-audit.jsonl</c> next to <c>cockpit.json</c> — one JSON
/// object per line, so it survives a restart, can be tailed while the app runs, and stays readable in a text
/// editor. Append-only: there is no write path here that rewrites or truncates the file, so a decision, once
/// logged, cannot be erased by a later action — which is the whole point of an audit a plugin cannot clear.
/// </summary>
internal sealed class ConsentAuditLog : IConsentAuditLog, ISingletonService
{
    /// <summary>The action literal is trimmed: the log is for recognising a decision later, not for keeping a full copy of every command.</summary>
    private const int MaxActionLength = 300;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _logFilePath;
    private readonly ILogger<ConsentAuditLog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConsentAuditLog(ILogger<ConsentAuditLog> logger)
        : this(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
    internal ConsentAuditLog(string logFilePath, ILogger<ConsentAuditLog> logger)
    {
        _logFilePath = logFilePath;
        _logger = logger;
    }

    public async Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(entry with { ActionText = _Trim(entry.ActionText) }, SerializerOptions);
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A broken audit log must not take the action down with it — losing the record is bad, blocking the
            // operator's approved action is worse. Logged rather than swallowed, so a silently unwritable log surfaces.
            _logger.LogWarning(ex, "Could not append to the consent audit log at {Path}.", _logFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
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
            _logger.LogWarning(ex, "Could not read the consent audit log at {Path}.", _logFilePath);
            return [];
        }
    }

    // A half-written or hand-edited line is skipped rather than throwing away the whole trail.
    private static ConsentAuditEntry? _TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<ConsentAuditEntry>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string _Trim(string actionText) => actionText is { Length: > MaxActionLength }
        ? actionText[..MaxActionLength] + "…"
        : actionText;

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "consent-audit.jsonl");
}
