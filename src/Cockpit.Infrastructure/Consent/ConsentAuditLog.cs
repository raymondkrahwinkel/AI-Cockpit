using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// Appends the consent audit trail (#AC-47) to <c>consent-audit.jsonl</c> next to <c>cockpit.json</c>. The
/// append-only, never-throws, JSON-per-line machinery — and the tail-read that keeps the last N without loading
/// the whole file — lives in <see cref="JsonlAuditLog{T}"/>; this only names the file and trims the action literal
/// so the log stays a record of what was decided, not a copy of every command.
/// </summary>
internal sealed class ConsentAuditLog : JsonlAuditLog<ConsentAuditEntry>, IConsentAuditLog, ISingletonService
{
    /// <summary>The action literal is trimmed: the log is for recognising a decision later, not for keeping a full copy of every command.</summary>
    private const int MaxActionLength = 300;

    public ConsentAuditLog(ILogger<ConsentAuditLog> logger)
        : base(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
    internal ConsentAuditLog(string logFilePath, ILogger<ConsentAuditLog> logger)
        : base(logFilePath, logger)
    {
    }

    protected override string LogName => "consent";

    protected override ConsentAuditEntry PrepareForWrite(ConsentAuditEntry entry) =>
        entry with { ActionText = TrimText(entry.ActionText, MaxActionLength) };

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "consent-audit.jsonl");
}
