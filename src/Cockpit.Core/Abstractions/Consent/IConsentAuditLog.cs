namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Records every consent decision — what was asked, by whom, and whether it was approved or denied (#AC-47).
/// Consent lets an agent act with the operator's rights, so "what did I approve while it was running" has to be
/// answerable from something more durable than the app's memory.
/// </summary>
/// <remarks>
/// Append-only by contract: there is no clear or delete here. A plugin — or an agent through it — must not be able
/// to erase its own consent trail, which is exactly the record you want when something went wrong. Denials are
/// recorded too, including the fail-closed ones where nothing could ask.
/// </remarks>
public interface IConsentAuditLog
{
    /// <summary>Appends an entry. Never throws: a broken audit log must not take the action down with it, so a write failure is a logged warning rather than a lost decision.</summary>
    Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first, for the audit view.</summary>
    Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

/// <summary>What the operator decided about a consent request (#AC-47).</summary>
public enum ConsentAuditAction
{
    /// <summary>The operator approved the action.</summary>
    Approved,

    /// <summary>The operator denied it, or it was denied without asking — no consent surface, or the request was cancelled (fail-closed).</summary>
    Denied,
}

/// <summary>One line of the consent audit trail (#AC-47).</summary>
/// <param name="SourceLabel">A short human name for who asked — "Workflows", "Terminal MCP".</param>
/// <param name="PaneId">The session the request belonged to, if any.</param>
/// <param name="PluginId">The plugin that asked, if it came through a plugin rather than a host-internal caller.</param>
/// <param name="Scope">The kind of action, the key a remembered approval is scoped by.</param>
/// <param name="ActionText">The literal action that was asked about, trimmed: the command, the URL, the pane — enough to recognise later.</param>
/// <param name="Remembered">True when the operator chose not to be asked again this session for this source and scope.</param>
public sealed record ConsentAuditEntry(
    DateTimeOffset At,
    ConsentAuditAction Action,
    string SourceLabel,
    string? PaneId,
    string? PluginId,
    string Scope,
    string ActionText,
    bool Remembered);
