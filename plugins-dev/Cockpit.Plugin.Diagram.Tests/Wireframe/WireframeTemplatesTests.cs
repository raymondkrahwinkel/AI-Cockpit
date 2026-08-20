using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-911 criterion 4: every template the wireframe soortkeuze ships parses without error, produces at least one
// screen, and — since WireframeParser.Parse itself resolves every goto: as it reads a line (WireframeParser.cs:167)
// — a dangling goto: shows up here as a parse error, not as a silently broken preview. One [Fact] looping the
// list rather than [Theory]/MemberData: SurfaceTemplate is internal, and a public theory method cannot take an
// internal parameter type.
public class WireframeTemplatesTests
{
    [Fact]
    public void EveryTemplate_ParsesWithNoErrorsAndAtLeastOneScreen()
    {
        foreach (var template in WireframeTemplates.All)
        {
            var result = WireframeParser.Parse(template.Source);

            Assert.True(result.Errors.Count == 0, $"{template.Name}: {string.Join(", ", result.Errors)}");
            Assert.True(result.HasScreens, $"{template.Name} produced no screens");
        }
    }

    [Fact]
    public void Blank_IsExactlyWireframeDocumentEmpty()
    {
        Assert.Equal(WireframeDocument.Empty, WireframeTemplates.Blank.Source);
    }
}
