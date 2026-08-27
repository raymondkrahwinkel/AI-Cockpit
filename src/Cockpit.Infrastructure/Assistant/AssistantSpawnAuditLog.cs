using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Assistant;

// Appends the assistant spawn trail (AC-545, criterion 5) to `assistant-spawn-audit.jsonl` via
// `JsonlAuditLog{T}`, trimming the free-text refusal field. Unbounded: no adversarial writer (the
// assistant itself writes this trail), and rollover for ~347 KB/6wk (AC-1128) was considered and not worth it.
internal sealed class AssistantSpawnAuditLog : JsonlAuditLog<AssistantSpawnAuditEntry>, IAssistantSpawnAuditLog, ISingletonService
{
    // The refusal reason is trimmed: the trail is for recognising what the gate stopped, not for keeping a full copy of every message a tool ever returned.
    private const int MaxRefusalLength = 300;

    public AssistantSpawnAuditLog(ILogger<AssistantSpawnAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.AssistantSpawn), logger)
    {
    }

    // Test seam: point the log at an arbitrary file.
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
