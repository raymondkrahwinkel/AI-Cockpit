using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1398: the workspace shows the session the backend names by pane id. A pane the host no longer knows shows
// nothing, and says so in one trace line however often the surface re-renders.
public class AutopilotSessionViewsTests
{
    [Fact]
    public void For_ShowsAKnownPaneAndTracesAnUnknownOneOnce()
    {
        var stepView = new TextBlock();
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateEmbeddedSessionView("step-pane").Returns(stepView);
        var traced = new List<string>();
        var views = new AutopilotSessionViews(host, traced.Add);

        var shown = (views.For("step-pane"), views.For("gone-pane"), views.For("gone-pane"));

        Assert.Equal((stepView, (Control?)null, (Control?)null), shown);
        Assert.Contains("gone-pane", Assert.Single(traced));
    }
}
