using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// Appends the agent-notify trail (AC-392) to <c>agent-notify-audit.jsonl</c> next to <c>cockpit.json</c>. The
/// append-only, never-throws, JSON-per-line machinery — and the tail-read that keeps the last N without loading the
/// whole file — lives in <see cref="JsonlAuditLog{T}"/>, the same base the consent trail uses; this only names the
/// file and trims the two free-text fields the sender controls, so one agent cannot make the trail unreadable by
/// sending a megabyte.
/// </summary>
internal sealed class AgentNotifyAuditLog : JsonlAuditLog<AgentNotifyAuditEntry>, IAgentNotifyAuditLog, ISingletonService
{
    /// <summary>The message body is trimmed: the trail is for recognising an attempt later, not for keeping a second copy of every message.</summary>
    private const int MaxBodyLength = 300;

    /// <summary>The kind is a short label by design, so anything past this is not a label — it is a body in the wrong field.</summary>
    private const int MaxKindLength = 100;

    public AgentNotifyAuditLog(ILogger<AgentNotifyAuditLog> logger)
        : base(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
    internal AgentNotifyAuditLog(string logFilePath, ILogger<AgentNotifyAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "agent notify";

    protected override AgentNotifyAuditEntry PrepareForWrite(AgentNotifyAuditEntry entry) =>
        entry with
        {
            Kind = TrimText(entry.Kind, MaxKindLength),
            Body = TrimText(entry.Body, MaxBodyLength),
        };

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "agent-notify-audit.jsonl");
}
