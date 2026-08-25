using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Consent;

// Appends the consent audit trail (#AC-47) to `consent-audit.jsonl` next to `cockpit.json`.
// Append-only/never-throws/tail-read machinery lives in `JsonlAuditLog{T}`; this only names the
// file and trims the action literal, so the log stays a record of what was decided, not every command.
internal sealed class ConsentAuditLog : JsonlAuditLog<ConsentAuditEntry>, IConsentAuditLog, ISingletonService
{
    // The action literal is trimmed: the log is for recognising a decision later, not for keeping a full copy of every command.
    private const int MaxActionLength = 300;

    public ConsentAuditLog(ILogger<ConsentAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.Consent), logger)
    {
    }

    // Test seam: point the log at an arbitrary file.
    internal ConsentAuditLog(string logFilePath, ILogger<ConsentAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "consent";

    protected override ConsentAuditEntry PrepareForWrite(ConsentAuditEntry entry) =>
        entry with { ActionText = TrimText(entry.ActionText, MaxActionLength) };
}
