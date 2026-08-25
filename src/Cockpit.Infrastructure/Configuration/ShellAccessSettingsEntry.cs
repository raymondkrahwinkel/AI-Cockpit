using Cockpit.Core.Shell;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `ShellAccessSettings` in the `shellAccess` section of `cockpit.json` (AC-1066) — the master
// switch, off unless the operator turned it on.
internal sealed class ShellAccessSettingsEntry
{
    public bool Enabled { get; set; }

    public static ShellAccessSettingsEntry FromDomain(ShellAccessSettings settings) => new() { Enabled = settings.Enabled };

    public ShellAccessSettings ToDomain() => new() { Enabled = Enabled };
}
