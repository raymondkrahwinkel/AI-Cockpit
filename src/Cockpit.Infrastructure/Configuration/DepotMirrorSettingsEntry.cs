using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `DepotMirrorSettings` in the `depotMirrorSettings` section of `cockpit.json` (AC-278).
// Separate from the `DepotMirrors` registry section, which lists the mirrors themselves.
internal sealed class DepotMirrorSettingsEntry
{
    public string? Root { get; set; }

    public static DepotMirrorSettingsEntry FromDomain(DepotMirrorSettings settings) => new() { Root = settings.Root };

    public DepotMirrorSettings ToDomain() => new() { Root = Root };
}
