using Cockpit.Core.Whiteboard;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `WhiteboardAccessSettings` in the `whiteboardAccess` section of `cockpit.json` (AC-823) — the
// master switch, off unless the operator turned it on. Mirrors DiagramAccessSettingsEntry (AC-810).
internal sealed class WhiteboardAccessSettingsEntry
{
    public bool Enabled { get; set; }

    public static WhiteboardAccessSettingsEntry FromDomain(WhiteboardAccessSettings settings) => new() { Enabled = settings.Enabled };

    public WhiteboardAccessSettings ToDomain() => new() { Enabled = Enabled };
}
