using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The seam the <c>cockpit-session</c> MCP server writes through when an agent labels its own session (#AC-312). An
/// agent may say what it is working on outright; the name it may only propose, so a session the operator named keeps
/// that name (#AC-310). Here rather than in the unit tests because the sink marshals to the UI thread: without a
/// pumping dispatcher, awaiting it never returns.
/// </summary>
[Collection("avalonia")]
public class SessionLabelSinkTests
{
    [Fact]
    public async Task TheStatuslineLandsOnThePaneThatWasNamed()
    {
        var (sink, session) = _SinkOnASession();

        var applied = await sink.SetStatuslineAsync(session.PaneId, "AC-312");

        Assert.True(applied);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("AC-312", session.Statusline));
    }

    [Fact]
    public async Task ASessionStillCarryingAMadeUpName_TakesTheOneProposed()
    {
        var (sink, session) = _SinkOnASession();

        var renamed = await sink.SuggestNameAsync(session.PaneId, "AC-312");

        Assert.True(renamed);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("AC-312", session.Title));
    }

    [Fact]
    public async Task ASessionNamedOnPurpose_KeepsItsName_AndSaysSo()
    {
        var (sink, session) = _SinkOnASession();
        Dispatcher.UIThread.Invoke(() =>
        {
            session.Title = "release work";
            session.HasGeneratedName = false;
        });

        var renamed = await sink.SuggestNameAsync(session.PaneId, "AC-312");

        // False is the whole point: the agent is told the name stood, rather than believing it renamed anything.
        Assert.False(renamed);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("release work", session.Title));
    }

    [Fact]
    public async Task APaneIdThatMatchesNothing_ReportsIt_RatherThanRenamingSomethingElse()
    {
        var (sink, _) = _SinkOnASession();

        Assert.False((await sink.SuggestNameAsync("no-such-pane", "AC-312")));
        Assert.False((await sink.SetStatuslineAsync("no-such-pane", "AC-312")));
    }

    private static (SessionLabelSink Sink, SessionPanelViewModel Session) _SinkOnASession() =>
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new SessionViewModel();
            cockpit.Sessions.Add(session);

            return (new SessionLabelSink(cockpit), (SessionPanelViewModel)session);
        });
}
