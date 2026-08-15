using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-807's definition of done, run against the five diagram sources the pilot on AC-525 used to prove the
/// fix: each must render with zero unresolved <c>var(</c>/<c>color-mix(</c> reaching the SVG — the cheap,
/// hard assertion the ticket calls out as the best regression test this work can get.
/// </summary>
public class MermaidRenderPipelineTests
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

    public static TheoryData<string, string> PilotSources() => new()
    {
        { "flowchart-with-subgraphs", FlowchartWithSubgraphs },
        { "sequence-with-alt-loop-par-notes", SequenceWithAltLoopParNotes },
        { "class-with-generics-and-relations", ClassWithGenericsAndRelations },
        { "state-v2", StateV2 },
        { "er-with-crows-foot", ErWithCrowsFoot },
    };

    [Theory]
    [MemberData(nameof(PilotSources))]
    public void Render_ProducesSvgWithNoUnresolvedVarOrColorMix(string label, string source)
    {
        var document = MermaidRenderPipeline.Render(source, Theme);

        Assert.DoesNotContain("var(", document.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("color-mix(", document.Markup, StringComparison.Ordinal);
        Assert.True(document.Width > 0 && document.Height > 0, $"{label}: expected a positive viewport size");
    }

    [Fact]
    public void Render_KeepsAUserSuppliedClassDefsLiteralHexUntouched()
    {
        const string source = """
            flowchart TD
                A[Start] --> B[Done]
                classDef highlight fill:#ffc107,stroke:#856404
                class B highlight
            """;

        var document = MermaidRenderPipeline.Render(source, Theme);

        Assert.Contains("#ffc107", document.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#856404", document.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private const string FlowchartWithSubgraphs = """
        flowchart TD
            subgraph Ingest
                A[Fetch] --> B[Parse]
            end
            subgraph Render
                C[Layout] --> D[Rasterize]
            end
            B --> C
            D --> E{OK?}
            E -->|yes| F[Done]
            E -->|no| A
        """;

    private const string SequenceWithAltLoopParNotes = """
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
            par notify
                S-->>U: Ping A
            and
                S-->>U: Ping B
            end
            Note over U,S: Session closed
        """;

    private const string ClassWithGenericsAndRelations = """
        classDiagram
            class Repository~T~ {
                +Add(T item) void
                +Get(id) T
            }
            class Entity
            class Cache~T~
            Repository~T~ --> Entity : uses
            Repository~T~ ..> Cache~T~ : depends on
            Entity <|-- User
            Entity *-- Address
        """;

    private const string StateV2 = """
        stateDiagram-v2
            [*] --> Idle
            Idle --> Running : start
            Running --> Paused : pause
            Paused --> Running : resume
            Running --> [*] : finish
        """;

    private const string ErWithCrowsFoot = """
        erDiagram
            CUSTOMER ||--o{ ORDER : places
            ORDER ||--|{ LINE_ITEM : contains
            CUSTOMER {
                string name
                string email
            }
        """;
}
