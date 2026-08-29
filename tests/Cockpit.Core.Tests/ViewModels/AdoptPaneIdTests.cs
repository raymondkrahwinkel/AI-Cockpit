using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-410 step 2: <see cref="SessionPanelViewModel.AdoptPaneId"/> is the one-time override that lets a restored
/// session keep the id it was persisted under. It is a single-use seam — a second call is a programming error
/// (a pane being restored twice), not something to silently allow.
/// </summary>
public class AdoptPaneIdTests
{
    [Fact]
    public void AdoptPaneId_CalledASecondTime_Throws()
    {
        var session = new SessionViewModel();
        session.AdoptPaneId("saved-pane-1");

        var exception = Assert.Throws<InvalidOperationException>(() => session.AdoptPaneId("saved-pane-2"));

        Assert.Contains("saved-pane-1", exception.Message);
        // The first adoption is not undone by the failed second attempt.
        Assert.Equal("saved-pane-1", session.PaneId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdoptPaneId_ANullOrBlankId_Throws(string? blank)
    {
        var session = new SessionViewModel();

        Assert.Throws<ArgumentException>(() => session.AdoptPaneId(blank!));
    }
}
