using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Cockpit.Plugin.Diagram.Collab;
using Mermaider;

namespace Cockpit.Plugin.Diagram;

// AC-911: kept to what DiagramObjectEdit.DialectOf (AC-899) can still hand-edit afterwards — sequence, class,
// state and gantt render fine but leave every hand-edit button disabled, so they stay out until DiagramObjectEdit
// grows another dialect. Blank is DiagramDocument.Empty itself, first and preselected (criterion 7).
internal static class DiagramTemplates
{
    public static readonly SurfaceTemplate Blank = new("Blank", DiagramDocument.Empty);

    public static readonly SurfaceTemplate Flowchart = new("Flowchart", """
        flowchart LR
            Start([Start]) --> Step[Do the work]
            Step --> Finish([Done])
        """);

    public static readonly SurfaceTemplate EntitiesAndRelations = new("Entities & relations", """
        erDiagram
            CUSTOMER {
                string name
                string email
            }
            ORDER {
                string id
                date placedAt
            }
            CUSTOMER ||--o{ ORDER : places
        """);

    public static readonly IReadOnlyList<SurfaceTemplate> All = [Blank, Flowchart, EntitiesAndRelations];

    // Same two calls DiagramWorkspaceBody._RenderInto makes, and the same theme (DiagramTheme) — a template
    // thumbnail is a diagram render like any other, just smaller.
    public static Control Preview(SurfaceTemplate template) => new Avalonia.Svg.Skia.Svg(baseUri: null!)
    {
        Stretch = Stretch.Uniform,
        SvgSource = SvgSource.LoadFromSvg(MermaidRenderer.RenderSvg(template.Source, DiagramTheme.Options)),
    };
}
