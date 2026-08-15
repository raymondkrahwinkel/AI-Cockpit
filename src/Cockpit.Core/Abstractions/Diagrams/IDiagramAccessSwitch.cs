namespace Cockpit.Core.Abstractions.Diagrams;

/// <summary>
/// The live value of the diagram-access master switch (AC-810), the diagram counterpart to
/// <c>ITerminalAccessSwitch</c> (AC-34). The endpoint fan-out reads it synchronously to decide whether
/// <c>cockpit-diagram</c> is advertised to a session at all; off by default (opt-in).
/// </summary>
public interface IDiagramAccessSwitch
{
    bool Enabled { get; set; }
}
