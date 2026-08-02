using Cockpit.Core.Clones;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `CloneSettings` in the `cloneSettings` section of `cockpit.json` (AC-90). Separate from the `clones` registry section, which lists the clones themselves.
internal sealed class CloneSettingsEntry
{
    public string? Root { get; set; }

    public static CloneSettingsEntry FromDomain(CloneSettings settings) => new() { Root = settings.Root };

    public CloneSettings ToDomain() => new() { Root = Root };
}
