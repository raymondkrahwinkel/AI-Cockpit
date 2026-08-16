using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-816 DoD: a session chosen at quick-start must come out of surface creation with zero capabilities —
// Couple, never Grant — so read_diagram/edit_diagram still ask their own consent.
public class DiagramQuickStartTests
{
    [Fact]
    public void ApplyTo_WithASession_OpensTheSurfaceAndCouplesWithoutGrantingAnyCapability()
    {
        var registry = new RecordingDiagramAccessRegistry();
        var quickStart = new DiagramQuickStart("Working title", "session-a");

        quickStart.ApplyTo(registry, "surface-1", "flowchart LR\nA-->B");

        Assert.Equal(("surface-1", "Working title", "flowchart LR\nA-->B"), registry.Opened);
        Assert.Equal(("session-a", "surface-1"), registry.Coupled);
        Assert.Empty(registry.Granted);
    }

    [Fact]
    public void ApplyTo_WithNoSession_OpensTheSurfaceWithoutCoupling()
    {
        var registry = new RecordingDiagramAccessRegistry();
        var quickStart = new DiagramQuickStart("Working title", null);

        quickStart.ApplyTo(registry, "surface-1", "flowchart LR\nA-->B");

        Assert.Equal(("surface-1", "Working title", "flowchart LR\nA-->B"), registry.Opened);
        Assert.Null(registry.Coupled);
    }

    private sealed class RecordingDiagramAccessRegistry : IDiagramAccessRegistry
    {
        public (string SurfaceId, string Name, string InitialText)? Opened { get; private set; }

        public (string SessionId, string SurfaceId)? Coupled { get; private set; }

        public List<(string SessionId, string SurfaceId, DiagramCapability Capability)> Granted { get; } = [];

        public event Action<string, string>? TextChanged;

        public event Action<DiagramCouplingChange>? CouplingChanged;

        public event Action<string, DiagramProposal?>? ProposalChanged;

        public void SurfaceOpened(string surfaceId, string name, string initialText) => Opened = (surfaceId, name, initialText);

        public void SurfaceClosed(string surfaceId)
        {
        }

        public void UpdateText(string surfaceId, string text) => TextChanged?.Invoke(surfaceId, text);

        public void Disconnect(string surfaceId)
        {
        }

        public string? PeekText(string surfaceId) => null;

        public IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId) => [];

        public DiagramSurface? Resolve(string surfaceRef) => null;

        public DiagramCoupling? CouplingOf(string sessionId, string surfaceId) => null;

        public bool IsCoupledByAnother(string sessionId, string surfaceId) => false;

        public void Couple(string sessionId, string surfaceId) => Coupled = (sessionId, surfaceId);

        public void Grant(string sessionId, string surfaceId, DiagramCapability capability) =>
            Granted.Add((sessionId, surfaceId, capability));

        public string? ReadCoupled(string sessionId, string surfaceId) => null;

        public bool WriteCoupled(string sessionId, string surfaceId, string text) => false;

        public void SessionEnded(string sessionId)
        {
        }

        public bool Propose(string sessionId, string surfaceId, string proposedText, string changeSummary, IReadOnlyList<string> fidelityFindings) => false;

        public DiagramProposal? PendingProposal(string surfaceId) => null;

        public bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks) => false;

        public bool DiscardProposal(string surfaceId) => false;
    }
}
