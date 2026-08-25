using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Auditing;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: appends the AC-392 notify trail to `agent-notify-audit.jsonl` (owner-only, next to cockpit.json)
// via `JsonlAuditLog{T}`, trimming the sender-controlled free-text fields. Deliberately keeps body text
// (up to 300 chars) and grows unbounded — see ticket comment for the full accepted-risk trade-off.
internal sealed class AgentNotifyAuditLog : JsonlAuditLog<AgentNotifyAuditEntry>, IAgentNotifyAuditLog, ISingletonService
{
    // The message body is trimmed: the trail is for recognising an attempt later, not for keeping a second copy of every message.
    private const int MaxBodyLength = 300;

    // The kind is a short label by design, so anything past this is not a label — it is a body in the wrong field.
    private const int MaxKindLength = 100;

    // A pane id the host minted is far shorter than this, so trimming never touches a real one; what it bounds is
    // the refusal path, where the addressee is a string the sending agent chose and nothing has validated.
    private const int MaxPaneIdLength = 200;

    public AgentNotifyAuditLog(ILogger<AgentNotifyAuditLog> logger)
        : base(AuditTrailFiles.InStateRoot(AuditTrailFiles.AgentNotify), logger)
    {
    }

    // Test seam: point the log at an arbitrary file.
    internal AgentNotifyAuditLog(string logFilePath, ILogger<AgentNotifyAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "agent notify";

    protected override AgentNotifyAuditEntry PrepareForWrite(AgentNotifyAuditEntry entry) =>
        entry with
        {
            ToPaneId = TrimText(entry.ToPaneId, MaxPaneIdLength),
            Kind = TrimText(entry.Kind, MaxKindLength),
            Body = TrimText(entry.Body, MaxBodyLength),
        };
}
