using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1398: the workspace shows the session the backend names by pane id. A view it placed stays on screen after the
// host lets its session go — as the run's own Control reference did — until no active run names that pane; a pane
// never seen shows nothing, and says so in one trace line however often the surface re-renders.
[Collection("avalonia")]
public class AutopilotSessionViewsTests
{
    [Fact]
    public void For_KeepsAPlacedViewUntilNoRunNamesIt_AndTracesANeverSeenPaneOnce()
    {
        var stepView = new TextBlock();
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateEmbeddedSessionView("step-pane").Returns(stepView, (Control?)null);
        var traced = new List<string>();
        var views = new AutopilotSessionViews(host, traced.Add);

        var live = views.For("step-pane");
        var afterClose = views.For("step-pane");
        var unknown = (views.For("gone-pane"), views.For("gone-pane"));
        views.Retain(["other-pane"]);
        var afterSettle = views.For("step-pane");

        Assert.Equal((stepView, stepView, ((Control?)null, (Control?)null), (Control?)null), (live, afterClose, unknown, afterSettle));
        Assert.Equal(2, traced.Count);
    }
}
