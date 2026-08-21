namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-960: the dock-rail registration, mirroring PullRequestBadgeUpdaterTests' own coverage of the same
// older-host guard shape for AC-516's AddSideMenuButtonWithBadge.
[Collection("avalonia")]
public class PullRequestDockPanelRegistrarTests
{
    private static PullRequestRefreshSource _Source() =>
        new(new InMemoryPluginStorage(), (_, _) => Task.FromResult(PullRequestFeedResult.Missing), TimeSpan.FromMinutes(10));

    [Fact]
    public void Register_AddsThePanel_WithTheStableIdAndTitle() => HeadlessAvalonia.Run(() =>
    {
        var host = new TestDockPanelHost();
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

        PullRequestDockPanelRegistrar.Register(host, settings, _Source());

        var panel = Assert.Single(host.RegisteredPanels);
        Assert.Equal("github.pull-requests", panel.Id);
        Assert.Equal("Pull Requests", panel.Title);
    });

    [Theory]
    [InlineData(typeof(MissingMethodException))]
    [InlineData(typeof(TypeLoadException))]
    public void AnOlderHostWithNoDockPanelSupport_DoesNotTakeThePluginDown(Type exceptionType) => HeadlessAvalonia.Run(() =>
    {
        var host = new TestDockPanelHost { DockPanelUnsupportedException = () => (Exception)Activator.CreateInstance(exceptionType)! };
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

        PullRequestDockPanelRegistrar.Register(host, settings, _Source());

        Assert.Empty(host.RegisteredPanels);
    });
}
