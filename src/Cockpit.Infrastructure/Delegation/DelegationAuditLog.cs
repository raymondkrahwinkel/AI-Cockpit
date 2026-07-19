using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Appends the delegation audit trail (#67) to <c>delegation-audit.jsonl</c> next to <c>cockpit.json</c>. The
/// append-only, never-throws, JSON-per-line machinery — and the tail-read that keeps the last N without loading
/// the whole file — lives in <see cref="JsonlAuditLog{T}"/>; this only names the file and trims the prompt so the
/// log stays a record of what was handed out, not a copy of every transcript.
/// </summary>
internal sealed class DelegationAuditLog : JsonlAuditLog<DelegationAuditEntry>, IDelegationAuditLog, ISingletonService
{
    /// <summary>Prompts are trimmed: the log is for recognising a task later, not for keeping a copy of every transcript.</summary>
    private const int MaxPromptLength = 300;

    public DelegationAuditLog(ILogger<DelegationAuditLog> logger)
        : base(_DefaultPath(), logger)
    {
    }

    /// <summary>Test seam: point the log at an arbitrary file.</summary>
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

    private static string _DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default) ?? string.Empty, "delegation-audit.jsonl");
}
