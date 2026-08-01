using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// Appends the assistant spawn trail (AC-545, criterion 5) to <c>assistant-spawn-audit.jsonl</c> next to
/// <c>cockpit.json</c>. The append-only, never-throws, JSON-per-line machinery — and the tail-read that keeps the
/// last N without loading the whole file — lives in <see cref="JsonlAuditLog{T}"/>, the same base the consent and
/// delegation trails use; this only names the file and trims the one free-text field a refusal can put arbitrary
/// length into, so the log stays a record of what was started (or refused), not a copy of every explanation.
/// </summary>
internal sealed class AssistantSpawnAuditLog : JsonlAuditLog<AssistantSpawnAuditEntry>, IAssistantSpawnAuditLog, ISingletonService
{
    /// <summary>The refusal reason is trimmed: the trail is for recognising what the gate stopped, not for keeping a full copy of every message a tool ever returned.</summary>
    private const int MaxRefusalLength = 300;

    public AssistantSpawnAuditLog(ILogger<AssistantSpawnAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.AssistantSpawn), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
    internal AssistantSpawnAuditLog(string logFilePath, ILogger<AssistantSpawnAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "assistant spawn";

    protected override AssistantSpawnAuditEntry PrepareForWrite(AssistantSpawnAuditEntry entry) =>
        entry.Refusal is { } refusal
            ? entry with { Refusal = TrimText(refusal, MaxRefusalLength) }
            : entry;
}
