namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1065: the dock-rail registration, mirroring PullRequestDockPanelRegistrarTests' own coverage of the same
// older-host guard shape.
public class CiWorkflowRunsDockPanelRegistrarTests
{
    [Fact]
    public void Register_AddsThePanel_WithTheStableIdAndTitle()
    {
        var host = new TestDockPanelHost();

        CiWorkflowRunsDockPanelRegistrar.Register(host);

        var panel = Assert.Single(host.RegisteredPanels);
        Assert.Equal("github.actions", panel.Id);
        Assert.Equal("GitHub Actions", panel.Title);
    }

    [Theory]
    [InlineData(typeof(MissingMethodException))]
    [InlineData(typeof(TypeLoadException))]
    public void AnOlderHostWithNoDockPanelSupport_DoesNotTakeThePluginDown(Type exceptionType)
    {
        var host = new TestDockPanelHost { DockPanelUnsupportedException = () => (Exception)Activator.CreateInstance(exceptionType)! };

        CiWorkflowRunsDockPanelRegistrar.Register(host);

        Assert.Empty(host.RegisteredPanels);
    }
}
