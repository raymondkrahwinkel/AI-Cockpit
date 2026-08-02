using Cockpit.Core.Terminal;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `TerminalAccessSettings` in the `terminalAccess` section of `cockpit.json` (AC-34) — the master switch, off unless the operator turned it on.
internal sealed class TerminalAccessSettingsEntry
{
    public bool Enabled { get; set; }

    public static TerminalAccessSettingsEntry FromDomain(TerminalAccessSettings settings) => new() { Enabled = settings.Enabled };

    public TerminalAccessSettings ToDomain() => new() { Enabled = Enabled };
}
