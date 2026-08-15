using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// The coupling rules behind the diagram-access MCP (AC-810): a surface's text is a state the registry owns from
/// the moment it opens (not a stream captured from the coupling on, unlike terminal), a coupling can exist with
/// zero capabilities, one agent per surface, and a surface close or a session end decouples on its own.
/// </summary>
public class DiagramAccessRegistryTests
{
    [Fact]
    public void Couple_OnItsOwn_GrantsNoCapabilities()
    {
        // AC-810 DoD: coupling without capabilities is a real, visible state — the shape AC-816's quick-start needs.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Couple("session-a", "surface-1");

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.NotNull(coupling);
        Assert.False(coupling!.CanRead);
        Assert.False(coupling.CanEdit);
        Assert.False(coupling.HasAnyCapability);
        Assert.Null(registry.ReadCoupled("session-a", "surface-1"));
        Assert.False(registry.WriteCoupled("session-a", "surface-1", "x"));
    }

    [Fact]
    public void ReadCoupled_ReturnsTheSurfaceAsItStandsNow_IncludingWhatWasThereBeforeTheCoupling()
    {
        // Deviation from AC-34: a diagram is a state, not a stream — there is no "since the coupling" to read from.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Couple("session-a", "surface-1");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);

        Assert.Equal("flowchart LR\nA-->B", registry.ReadCoupled("session-a", "surface-1"));
    }

    [Fact]
    public void Grant_Edit_AlsoGrantsRead()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.True(coupling!.CanRead);
        Assert.True(coupling.CanEdit);
    }

    [Fact]
    public void Couple_IsExclusive_ASecondAgentIsRefused_EvenAgainstAZeroCapabilityCoupling()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Couple("session-a", "surface-1");

        Assert.True(registry.IsCoupledByAnother("session-b", "surface-1"));
        Assert.Null(registry.CouplingOf("session-b", "surface-1"));
        var act = () => registry.Couple("session-b", "surface-1");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void WriteCoupled_UpdatesTheTextAndRaisesTextChanged_ButOnlyForASessionHoldingEdit()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<(string SurfaceId, string Text)>();
        registry.TextChanged += (surfaceId, text) => changes.Add((surfaceId, text));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read); // read only, not edit

        Assert.False(registry.WriteCoupled("session-a", "surface-1", "flowchart LR\nA-->C"));
        Assert.Empty(changes);

        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        Assert.True(registry.WriteCoupled("session-a", "surface-1", "flowchart LR\nA-->C"));

        Assert.Equal(("surface-1", "flowchart LR\nA-->C"), Assert.Single(changes));
        Assert.Equal("flowchart LR\nA-->C", registry.PeekText("surface-1"));
    }

    [Fact]
    public void PeekText_ReadsRegardlessOfCoupling_SoTheConsentPromptCanNameWhatIsBeingShared()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1"));
        Assert.Null(registry.PeekText("no-such-surface"));
    }

    [Fact]
    public void SurfaceClosed_DecouplesAutomatically()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        registry.SurfaceClosed("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.PeekText("surface-1"));
    }

    [Fact]
    public void SessionEnded_DecouplesEverySurfaceThatSessionHeld()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Flow one", "flowchart LR\nA-->B");
        registry.SurfaceOpened("surface-2", "Flow two", "flowchart LR\nC-->D");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);
        registry.Grant("session-a", "surface-2", DiagramCapability.Read);

        registry.SessionEnded("session-a");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.CouplingOf("session-a", "surface-2"));
    }

    [Fact]
    public void Resolve_MatchesByIdOrByOperatorFacingName()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        Assert.Equal("Onboarding flow", registry.Resolve("surface-1")!.Name);
        Assert.Equal("surface-1", registry.Resolve("Onboarding flow")!.SurfaceId);
        Assert.Null(registry.Resolve("nope"));
    }

    [Fact]
    public void Disconnect_DecouplesWhateverCapabilitiesWereHeld_AndAnnounces()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<DiagramCouplingChange>();
        registry.CouplingChanged += changes.Add;
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        registry.Disconnect("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Equal(2, changes.Count);
        Assert.NotNull(changes[0].Coupling);
        Assert.Null(changes[1].Coupling);
    }

    [Fact]
    public void Grant_RefusesASurfaceThatIsNotOpen()
    {
        var registry = new DiagramAccessRegistry();
        var act = () => registry.Grant("session-a", "never-registered", DiagramCapability.Read);
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void UpdateText_FromTheOperator_KeepsWhatAnAgentReadsInStep_AndRaisesTextChanged()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<(string SurfaceId, string Text)>();
        registry.TextChanged += (surfaceId, text) => changes.Add((surfaceId, text));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);

        registry.UpdateText("surface-1", "flowchart LR\nA-->B-->C");

        Assert.Equal("flowchart LR\nA-->B-->C", registry.ReadCoupled("session-a", "surface-1"));
        Assert.Equal(("surface-1", "flowchart LR\nA-->B-->C"), Assert.Single(changes));
    }
}
