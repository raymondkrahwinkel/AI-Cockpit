using Cockpit.Core.Terminal;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="TerminalAccessSettings"/> in the <c>terminalAccess</c> section of <c>cockpit.json</c> (AC-34) — the master switch, off unless the operator turned it on.</summary>
internal sealed class TerminalAccessSettingsEntry
{
    public bool Enabled { get; set; }

    public static TerminalAccessSettingsEntry FromDomain(TerminalAccessSettings settings) => new() { Enabled = settings.Enabled };

    public TerminalAccessSettings ToDomain() => new() { Enabled = Enabled };
}
