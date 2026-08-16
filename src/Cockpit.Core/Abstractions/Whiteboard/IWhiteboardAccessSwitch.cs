namespace Cockpit.Core.Abstractions.Whiteboard;

/// <summary>
/// The live value of the whiteboard-access master switch (AC-823), the whiteboard counterpart to
/// <c>IDiagramAccessSwitch</c> (AC-810). The endpoint fan-out reads it synchronously to decide whether
/// <c>cockpit-whiteboard</c> is advertised to a session at all; off by default (opt-in).
/// </summary>
public interface IWhiteboardAccessSwitch
{
    bool Enabled { get; set; }
}
