using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// The live value of the diagram-access master switch (AC-810), read synchronously by the endpoint fan-out to
// decide whether the `cockpit-diagram` server is advertised to a session at all. Off by default. Mirrors
// TerminalAccessState (AC-34).
internal sealed class DiagramAccessState : IDiagramAccessSwitch, ISingletonService
{
    public bool Enabled { get; set; }
}
