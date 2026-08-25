namespace Cockpit.Core.Abstractions.Shell;

/// <summary>
/// The live value of the shell-access master switch (AC-1066), reachable from the app layer so the Options toggle
/// can flip it and startup can seed it from the persisted setting. The endpoint fan-out reads it synchronously to
/// decide whether <c>cockpit-shell</c> is advertised to a session at all; off by default (opt-in).
/// </summary>
public interface IShellAccessSwitch
{
    bool Enabled { get; set; }
}
