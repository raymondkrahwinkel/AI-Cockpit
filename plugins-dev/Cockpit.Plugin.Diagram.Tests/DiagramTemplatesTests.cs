using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Mermaider;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-911 criterion 2: the diagram type picker may only ever offer what DiagramObjectEdit.DialectOf (AC-899) can
// still hand-edit afterwards. One [Fact] looping the list rather than [Theory]/MemberData: SurfaceTemplate is
// internal, and a public theory method cannot take an internal parameter type.
public class DiagramTemplatesTests
{
    [Fact]
    public void EveryTemplate_HasASupportedDialect()
    {
        foreach (var template in DiagramTemplates.All)
        {
            Assert.NotEqual(DiagramEditDialect.Unsupported, DiagramObjectEdit.DialectOf(template.Source));
        }
    }

    [Fact]
    public void EveryTemplate_RendersWithoutThrowing()
    {
        foreach (var template in DiagramTemplates.All)
        {
            var markup = MermaidRenderer.RenderSvg(template.Source, DiagramTheme.Options);

            Assert.False(string.IsNullOrWhiteSpace(markup));
        }
    }

    [Fact]
    public void Blank_IsExactlyDiagramDocumentEmpty()
    {
        Assert.Equal(DiagramDocument.Empty, DiagramTemplates.Blank.Source);
    }
}
