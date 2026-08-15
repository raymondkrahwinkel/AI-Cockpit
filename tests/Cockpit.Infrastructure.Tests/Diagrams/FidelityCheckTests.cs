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
    public void Render_NamesEveryTransitionMermaiderDropsAroundACompositeState()
    {
        var fidelity = MermaidRenderPipeline.Render(CompositeState, Theme).Fidelity;

        Assert.False(fidelity.IsComplete);
        var finding = Assert.Single(fidelity.Findings);
        Assert.StartsWith("4 of 11 connections in the source were not drawn:", finding, StringComparison.Ordinal);
        Assert.Contains("Idle --> Watching : arm", finding, StringComparison.Ordinal);
        Assert.Contains("Watching --> Driving : trigger", finding, StringComparison.Ordinal);
        Assert.Contains("Driving --> Idle : done", finding, StringComparison.Ordinal);
        Assert.Contains("Watching --> Idle : disarm", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AlsoCatchesTheCircleAndCrossLinksMermaiderDropsFromAFlowchart()
    {
        // Not the failure AC-808 was raised for — found by the detector itself, which is the point of
        // building a net for the class instead of a patch for the one reported case.
        const string source = """
            flowchart LR
                A[Start] --> B(Round)
                B --o C((Circle))
                C --x A
            """;

        var fidelity = MermaidRenderPipeline.Render(source, Theme).Fidelity;

        var finding = Assert.Single(fidelity.Findings);
        Assert.StartsWith("2 of 3 connections in the source were not drawn:", finding, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SoundSources))]
    public void Render_StaysSilentOnASourceThatCameThroughWhole(string label, string source)
    {
        var fidelity = MermaidRenderPipeline.Render(source, Theme).Fidelity;

        Assert.True(fidelity.IsComplete, $"{label}: expected no findings, got {string.Join(" | ", fidelity.Findings)}");
    }

    [Fact]
    public void Check_ReportsANoteThatNeverReachedTheSvg()
    {
        const string source = """
            stateDiagram-v2
                Idle --> Busy : go
                note right of Idle
                    waiting to be armed
                end note
                note left of Busy : working
            """;
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg"><path class="edge" data-from="Idle" data-to="Busy"/><g class="note"/></svg>
            """;

        var fidelity = FidelityCheck.Check(source, svg);

        Assert.Equal("1 of 2 notes in the source were not drawn.", Assert.Single(fidelity.Findings));
    }

    [Fact]
    public void Check_FallsBackToTheCountWhenItCannotPinDownWhichConnectionWentMissing()
    {
        // One connection short, but the one that was drawn matches neither source line — the pair matching
        // and the count disagree, so naming a line would be a guess.
        const string source = """
            flowchart TD
                A --> B
                C --> D
            """;
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg"><path class="edge" data-from="X" data-to="Y"/></svg>
            """;

        var fidelity = FidelityCheck.Check(source, svg);

        Assert.Equal(
            "1 of 2 connections in the source were not drawn (which ones could not be determined).",
            Assert.Single(fidelity.Findings));
    }

    [Fact]
    public void Check_DoesNotCountAConnectionTwiceBecauseItCarriesALabel()
    {
        // Mermaider repeats data-from/data-to on the edge's label group. Counting both would inflate the
        // drawn side and hide a real drop.
        const string source = """
            flowchart TD
                A -->|yes| B
            """;
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg"><path class="edge" data-from="A" data-to="B" data-label="yes"/><g class="edge-label" data-from="A" data-to="B" data-label="yes"/></svg>
            """;

        Assert.True(FidelityCheck.Check(source, svg).IsComplete);

        // And with the edge itself gone the label group left behind must not stand in for it.
        const string labelOnly = """
            <svg xmlns="http://www.w3.org/2000/svg"><g class="edge-label" data-from="A" data-to="B" data-label="yes"/></svg>
            """;
        Assert.Single(FidelityCheck.Check(source, labelOnly).Findings);
    }

    public static TheoryData<string, string> SoundSources() => new()
    {
        {
            "flowchart with subgraphs and edge labels", """
            flowchart TD
                subgraph Ingest
                    A[Fetch] --> B[Parse]
                end
                B --> C[Layout]
                C --> D{OK?}
                D -->|yes| E[Done]
                D -->|no| A
            """
        },
        {
            "flowchart with shapes and a mid-link label", """
            flowchart LR
                A[Start] -- carries text --> B(Round)
                B -.-> C{{Hex}}
                C ==> D[(Db)]
            """
        },
        {
            // 'Echo' ends in the same letter that ends a '--o' link; the scan must not eat it.
            "flowchart without spaces around its links", """
            graph LR
                Echo-->Bar
                my-node-->Echo
            """
        },
        {
            "sequence with alt/loop/par and a note", """
            sequenceDiagram
                participant U as User
                participant S as Service
                U->>S: Request
                alt success
                    S-->>U: 200 OK
                else failure
                    S-->>U: 500 Error
                end
                loop retry
                    U->>S: Retry
                end
                Note over U,S: Session closed
            """
        },
        {
            "class with generics, members and four relation kinds", """
            classDiagram
                class Repository~T~ {
                    +Add(T item) void
                    -count : int
                }
                class Entity
                Repository~T~ --> Entity : uses
                Entity <|-- User
                Entity *-- Address
                Entity o-- Tag
            """
        },
        {
            "ER with crow's foot cardinality and an attribute block", """
            erDiagram
                CUSTOMER ||--o{ ORDER : places
                PERSON }|..|{ CAR : owns
                CUSTOMER {
                    string name
                    string email
                }
            """
        },
        {
            // The block note's body holds an arrow that is prose, not a transition.
            "state with a block note and a one-line note", """
            stateDiagram-v2
                [*] --> Idle
                Idle --> Busy : go
                Busy --> Idle : done
                note right of Idle
                    waiting --> here
                end note
                note left of Busy : working
            """
        },
        {
            "flowchart with classDef, style and linkStyle", """
            flowchart TD
                A[Start] --> B[Done]
                classDef highlight fill:#ffc107,stroke:#856404
                class B highlight
                style A fill:#f9f,stroke:#333,stroke-width:4px
                linkStyle 0 stroke:#f00
            """
        },
        {
            // '---' opens front matter here; the same token is a link everywhere else.
            "flowchart behind YAML front matter", """
            ---
            title: My Diagram
            ---
            flowchart TD
                A --> B
                B --> C
            """
        },
        {
            "flowchart with a directive and a comment holding an arrow", """
            %%{init: {'theme':'dark'}}%%
            flowchart TD
                %% this comment mentions A --> B
                A --> B
            """
        },
        { "pie, which has no connections at all", "pie title Pets\n    \"Dogs\" : 386\n    \"Cats\" : 85" },
        {
            "gantt, whose dates are full of hyphens", """
            gantt
                title A Schedule
                dateFormat YYYY-MM-DD
                section One
                Task A :a1, 2026-01-01, 30d
                Task B :after a1, 20d
            """
        },
        { "mindmap, whose children are indentation", "mindmap\n    root((Cockpit))\n        Sessions\n            Agents\n        Plugins" },
    };

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
