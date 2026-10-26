using Cockpit.Plugins.Abstractions.Capabilities;

namespace Cockpit.App.ViewModels;

// What the first-load consent dialog shows before a plugin is enabled (#14): name/version/author, the folder,
// and the SHA-256 pinned on consent, plus the catalogue summary of each capability it asks for (AC-1730).
// The dialog also states that a plugin runs with the operator's own permissions.
public sealed record PluginConsentInfo(
    string DisplayName, string Version, string? Author, string FolderPath, string Sha256, IReadOnlyList<string>? Capabilities = null)
{
    public IReadOnlyList<string> CapabilityLines { get; } = [.. (Capabilities ?? []).Select(id => CapabilityCatalog.Find(id)?.Summary ?? id)];

    public bool HasCapabilities => CapabilityLines.Count > 0;
}
