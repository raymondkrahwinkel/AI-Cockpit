namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>
/// The live value of the terminal-access master switch (AC-34), reachable from the app layer so the Options toggle can
/// flip it and startup can seed it from the persisted setting. The endpoint fan-out reads it synchronously to decide
/// whether <c>cockpit-terminal</c> is advertised to a session at all; off by default (opt-in).
/// </summary>
public interface ITerminalAccessSwitch
{
    bool Enabled { get; set; }
}
