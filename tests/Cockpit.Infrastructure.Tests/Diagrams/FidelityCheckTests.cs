using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-808's definition of done: the pilot source with composite states must produce an explicit report that
/// connections were left out, and a sound source must produce nothing at all — a detector that squeaks at
/// everything gets clicked away and is then worse than no detector.
/// </summary>
public class FidelityCheckTests
{
    private static readonly MermaidTheme Theme = new(
        Bg: "#0f1116",
        Fg: "#e8eaef",
        Line: "#2a2f39",
        Accent: "#2563eb",
        Muted: "#949aa5",
        Surface: "#202430",
        Border: "#2a2f39",
        FontSizePx: 13);

    [Fact]
    public void Render_ReportsNoMissingConnectionWhenMermaiderDrawsCompositeStateTransitions()
    {
        var fidelity = MermaidRenderPipeline.Render(CompositeState, Theme).Fidelity;

        Assert.True(fidelity.IsComplete);
        Assert.Empty(fidelity.Findings);

        var dropped = FidelityCheck.Check("stateDiagram-v2\n    A --> B : missing", "<svg />");

        Assert.False(dropped.IsComplete);
        var finding = Assert.Single(dropped.Findings);
        Assert.Contains("A --> B : missing", finding, StringComparison.Ordinal);
    }

    private const string CompositeState = """
        stateDiagram-v2
            [*] --> Idle
            Idle --> Watching : arm
            Watching --> Driving : trigger
            Driving --> Idle : done
            Watching --> Idle : disarm
            state Watching {
                [*] --> Scanning
                Scanning --> Locked : match
                Locked --> Scanning : lost
            }
            state Driving {
                [*] --> Accelerating
                Accelerating --> Cruising : at speed
            }
            Idle --> [*]
            note right of Idle
                waiting for arm
            end note
        """;

}
