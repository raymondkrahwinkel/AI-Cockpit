namespace Cockpit.App.Plugins;

// A plugin discovered as `NeedsConsent` (#14/AC-208): new, or its bytes changed since approval. Kept
// separate from `PluginFailure` — an expected everyday state, not a failure — so it carries no `PluginIssueSeverity`.
public sealed record PluginPendingApproval(string FolderId, string DisplayName);
