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
/// file and trims the three free-text fields the sender controls, so one agent cannot make the trail unreadable by
/// sending a megabyte. The addressee is one of those three: on a refused attempt <c>ToPaneId</c> is whatever string
/// the agent passed and not a pane id the host vouches for, so it is no more bounded than the kind or the body.
/// The sender is not trimmed — it is stamped host-side from the verified pane, or null.
/// <para>
/// <strong>What is on disk, and who can read it.</strong> The file sits in the app state root
/// (<c>%APPDATA%\Cockpit</c>, <c>~/.config/Cockpit</c>) beside <c>cockpit.json</c>, and the base class creates it
/// owner-only. It holds no credential the cockpit put there, but it does hold up to 300 characters of every message body
/// an agent sent — free text, so nothing stops an agent from putting a token or a customer's name in one, and the trail
/// would then keep it. That is the deliberate trade: a record of what one agent told another is worth little if it does
/// not include what was said, and the answer to "that could be sensitive" is the file's permissions rather than a
/// redaction the host has no way to perform correctly. The trail also grows without bound, one line per attempt, and
/// notify is not rate-limited — an agent in a loop can make it large. Accepted for the same reason: an agent that wants
/// to fill the operator's disk has a shell and does not need this file to do it, and a trail that rotates is a trail
/// that can be made to forget.
/// </para>
/// </summary>
internal sealed class AgentNotifyAuditLog : JsonlAuditLog<AgentNotifyAuditEntry>, IAgentNotifyAuditLog, ISingletonService
{
    /// <summary>The message body is trimmed: the trail is for recognising an attempt later, not for keeping a second copy of every message.</summary>
    private const int MaxBodyLength = 300;

    /// <summary>The kind is a short label by design, so anything past this is not a label — it is a body in the wrong field.</summary>
    private const int MaxKindLength = 100;

    /// <summary>
    /// A pane id the host minted is far shorter than this, so trimming never touches a real one; what it bounds is
    /// the refusal path, where the addressee is a string the sending agent chose and nothing has validated.
    /// </summary>
    private const int MaxPaneIdLength = 200;

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
            ToPaneId = TrimText(entry.ToPaneId, MaxPaneIdLength),
            Kind = TrimText(entry.Kind, MaxKindLength),
            Body = TrimText(entry.Body, MaxBodyLength),
        };

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "agent-notify-audit.jsonl");
}
