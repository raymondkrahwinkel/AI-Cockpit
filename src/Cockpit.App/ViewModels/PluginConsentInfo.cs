namespace Cockpit.App.ViewModels;

/// <summary>
/// What the first-load consent dialog shows before a plugin is enabled (#14): the identity the operator
/// is trusting (name/version/author), where it lives on disk, and the SHA-256 of its entry assembly that
/// gets pinned on consent. The dialog also states that a plugin runs with the operator's own permissions.
/// </summary>
public sealed record PluginConsentInfo(string DisplayName, string Version, string? Author, string FolderPath, string Sha256);
