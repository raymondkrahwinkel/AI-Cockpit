namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Records every consent decision — what was asked, by whom, approved or denied (#AC-47) — so "what did I approve
/// while it was running" is answerable from something more durable than the app's memory. Append-only by
/// contract: no clear or delete, so a plugin (or an agent through it) can't erase its own trail. Denials, including fail-closed ones where nothing could ask, are recorded too.
/// </summary>
public interface IConsentAuditLog
{
    /// <summary>
    /// Appends an entry. Never throws: a broken audit log must not take the action down with it, so a write failure is a logged warning rather than a lost decision.
    /// </summary>
    Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent entries, newest first, for the audit view.
    /// </summary>
    Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// What the operator decided about a consent request (#AC-47).
public enum ConsentAuditAction
{
    // The operator approved the action.
    Approved,

    // The operator denied it, or it was denied without asking — no consent surface, or the request was cancelled (fail-closed).
    Denied,

    // AC-1013: Bypassed is its own value (not Approved+flag) so the audit trail can tell a pre-approved
    // bypass (#AC-575) from a real approval; it was added last, and an older build hits a per-line
    // JsonException on it, dropping only that entry.
    Bypassed,
}

// One line of the consent audit trail (#AC-47): SourceLabel = who asked, PaneId = the session (if any),
// PluginId = the plugin that asked (if any), Scope = the key an approval is remembered by, ActionText =
// the trimmed command/URL/pane, Remembered = true when the operator opted out of being asked again.
public sealed record ConsentAuditEntry(
    DateTimeOffset At,
    ConsentAuditAction Action,
    string SourceLabel,
    string? PaneId,
    string? PluginId,
    string Scope,
    string ActionText,
    bool Remembered);
