namespace Cockpit.Plugin.Diagram.Collab;

// What PresenceIndicators needs from whatever registry backs a surface, on top of ISurfaceActivityJournal (AC-879):
// whether an agent is coupled right now and holds any capability. Split out rather than folded into
// ISurfaceActivityJournal so ActivityStrip — which never looks at coupling — keeps depending on exactly what it uses.
internal interface ISurfaceCouplingSource
{
    // surfaceId, coupled, hasCapability. Flattened because DiagramCoupling and WhiteboardCoupling differ in shape;
    // hasCapability collapses to CanRead, which granting Edit/Write always sets alongside.
    event Action<string, bool, bool>? CouplingChanged;
}
