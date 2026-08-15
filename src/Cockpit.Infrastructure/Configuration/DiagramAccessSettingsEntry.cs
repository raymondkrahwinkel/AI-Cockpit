using Cockpit.Core.Diagrams;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `DiagramAccessSettings` in the `diagramAccess` section of `cockpit.json` (AC-810) — the master
// switch, off unless the operator turned it on. Mirrors TerminalAccessSettingsEntry (AC-34).
internal sealed class DiagramAccessSettingsEntry
{
    public bool Enabled { get; set; }

    public static DiagramAccessSettingsEntry FromDomain(DiagramAccessSettings settings) => new() { Enabled = settings.Enabled };

    public DiagramAccessSettings ToDomain() => new() { Enabled = Enabled };
}
