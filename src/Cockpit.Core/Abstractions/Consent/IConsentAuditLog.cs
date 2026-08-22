namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Records every consent decision — what was asked, by whom, approved or denied (#AC-47) — so "what did I approve
/// while it was running" is answerable from something more durable than the app's memory. Append-only by contract:
/// no clear or delete, so a plugin (or an agent through it) can't erase its own trail. Denials are recorded too,
/// including fail-closed ones where nothing could ask.
/// </summary>
public interface IConsentAuditLog
{
    /// <summary>Appends an entry. Never throws: a broken audit log must not take the action down with it, so a write failure is a logged warning rather than a lost decision.</summary>
    Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first, for the audit view.</summary>
    Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// What the operator decided about a consent request (#AC-47).
public enum ConsentAuditAction
{
    // The operator approved the action.
    Approved,

    // The operator denied it, or it was denied without asking — no consent surface, or the request was cancelled (fail-closed).
    Denied,

    // Nobody was asked: the operator had switched the assistant's consent bypass on for this source beforehand
    // (#AC-575), so the card never appeared. Its own value rather than an `Approved` with a flag —
    // the window that answers "what has this thing ever done" has to be able to tell an approval the operator
    // gave from one they had clicked away in advance.
    // Added last on purpose. The value is persisted by name, but an older build reading a trail that contains it
    // gets a `System.Text.Json.JsonException` on that line — which `JsonlAuditLog` catches per
    // line, so the unknown value costs that one entry and not the rest of the trail.
    Bypassed,
}

// One line of the consent audit trail (#AC-47).
//
// `SourceLabel`: A short human name for who asked — "Workflows", "Terminal MCP".
// `PaneId`: The session the request belonged to, if any.
// `PluginId`: The plugin that asked, if it came through a plugin rather than a host-internal caller.
// `Scope`: The kind of action, the key a remembered approval is scoped by.
// `ActionText`: The literal action that was asked about, trimmed: the command, the URL, the pane — enough to recognise later.
// `Remembered`: True when the operator chose not to be asked again this session for this source and scope.
public sealed record ConsentAuditEntry(
    DateTimeOffset At,
    ConsentAuditAction Action,
    string SourceLabel,
    string? PaneId,
    string? PluginId,
    string Scope,
    string ActionText,
    bool Remembered);
