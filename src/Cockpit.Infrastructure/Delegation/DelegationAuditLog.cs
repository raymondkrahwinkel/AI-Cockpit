using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Delegation;

// Appends the delegation audit trail (#67) to `delegation-audit.jsonl` via `JsonlAuditLog{T}`,
// trimming the prompt. Deliberately unbounded forever (AC-1128), same reasoning as the consent
// trail: a handed-out task, once logged, must not be erasable — see `SharedAuditTrailsDoNotRotateTests`.
internal sealed class DelegationAuditLog : JsonlAuditLog<DelegationAuditEntry>, IDelegationAuditLog, ISingletonService
{
    // Prompts are trimmed: the log is for recognising a task later, not for keeping a copy of every transcript.
    private const int MaxPromptLength = 300;

    public DelegationAuditLog(ILogger<DelegationAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.Delegation), logger)
    {
    }

    // Test seam: point the log at an arbitrary file.
    internal DelegationAuditLog(string logFilePath, ILogger<DelegationAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "delegation";

    // A null prompt stays null; a present one is trimmed surrogate-safely by the shared base (C5) — the char-index
    // trim this used to carry could leave a lone surrogate persisted as U+FFFD.
    protected override DelegationAuditEntry PrepareForWrite(DelegationAuditEntry entry) =>
        entry.Prompt is { } prompt
            ? entry with { Prompt = TrimText(prompt, MaxPromptLength) }
            : entry;
}
