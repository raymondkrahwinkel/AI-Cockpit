using Avalonia;
using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-860: a diagram is rendered in the host's own colours, read at render time — not a palette copied from the dark
// theme once. The headless app carries no Theme.axaml, so the token is planted here and taken away again.
[Collection("avalonia")]
public class DiagramThemeTests
{
    [Fact]
    public void Options_ReadTheHostsTokensAtRenderTime()
    {
        var resources = Application.Current!.Resources;
        resources["CockpitPanelBgBrush"] = new SolidColorBrush(Color.Parse("#ffffff"));
        try
        {
            Assert.Equal("#ffffff", DiagramTheme.Options.Bg);
        }
        finally
        {
            resources.Remove("CockpitPanelBgBrush");
        }

        Assert.Equal("#1a1d24", DiagramTheme.Options.Bg);
    }
}
