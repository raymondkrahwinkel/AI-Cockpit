namespace Cockpit.App.Plugins;

/// <summary>
/// A plugin discovered as <see cref="Cockpit.Core.Plugins.PluginLoadDecision.NeedsConsent"/> (#14/AC-208): new,
/// or its bytes changed since it was last approved. Kept separate from <see cref="PluginFailure"/> — this is not
/// a failure, it is an expected, everyday state the operator can clear from the Plugin store — so it does not
/// carry a <see cref="PluginIssueSeverity"/>.
/// </summary>
public sealed record PluginPendingApproval(string FolderId, string DisplayName);
