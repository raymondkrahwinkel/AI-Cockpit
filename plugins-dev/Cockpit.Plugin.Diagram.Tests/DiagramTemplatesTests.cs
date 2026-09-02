using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Mermaider;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-911 criterion 2: the diagram type picker may only ever offer what DiagramObjectEdit.DialectOf (AC-899) can
// still hand-edit afterwards. `SurfaceTemplate` is internal, so the rows carry the two fields these tests read
// rather than the template itself — a public test method may not name an internal type in its signature (CS0051).
// That also makes every template a case of its own, where the loop this replaced stopped at the first that broke.
public class DiagramTemplatesTests
{
    public static IEnumerable<object[]> Templates() =>
        DiagramTemplates.All.Select(template => new object[] { template.Name, template.Source });

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplate_HasASupportedDialect(string name, string source) =>
        Assert.True(
            DiagramObjectEdit.DialectOf(source) != DiagramEditDialect.Unsupported,
            $"the \"{name}\" template is offered by the picker but cannot be hand-edited afterwards");

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplate_RendersWithoutThrowing(string name, string source) =>
        Assert.True(
            !string.IsNullOrWhiteSpace(MermaidRenderer.RenderSvg(source, DiagramTheme.Options)),
            $"the \"{name}\" template rendered to nothing");

    [Fact]
    public void Blank_IsExactlyDiagramDocumentEmpty()
    {
        Assert.Equal(DiagramDocument.Empty, DiagramTemplates.Blank.Source);
    }
}
