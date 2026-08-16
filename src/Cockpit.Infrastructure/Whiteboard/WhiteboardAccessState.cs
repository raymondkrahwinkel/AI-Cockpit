using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Infrastructure.Whiteboard;

// The live value of the whiteboard-access master switch (AC-823), read synchronously by the endpoint fan-out to
// decide whether the `cockpit-whiteboard` server is advertised to a session at all. Off by default. Mirrors
// DiagramAccessState (AC-810).
internal sealed class WhiteboardAccessState : IWhiteboardAccessSwitch, ISingletonService
{
    public bool Enabled { get; set; }
}
