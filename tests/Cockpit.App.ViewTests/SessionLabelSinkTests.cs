using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What a handle does to the pane it already carries when an agent labels its session (#AC-312, #AC-310) — here
/// rather than in the unit tests because it marshals to the UI thread. AC-1374: the pane-id lookup and "no such
/// pane" refusal moved with <c>SessionLabelSink</c> to Infrastructure (<c>Cockpit.Backend.Tests</c>).
/// </summary>
[Collection("avalonia")]
public class SessionLabelSinkTests
{
    [Fact]
    public async Task TheStatuslineLandsOnThePaneThatWasNamed()
    {
        var (handle, session) = _HandleOnASession();

        var applied = await handle.SetStatuslineAsync("AC-312");

        Assert.True(applied);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("AC-312", session.Statusline));
    }

    [Fact]
    public async Task ASessionStillCarryingAMadeUpName_TakesTheOneProposed()
    {
        var (handle, session) = _HandleOnASession();

        var renamed = await handle.SuggestNameAsync("AC-312");

        Assert.True(renamed);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("AC-312", session.Title));
    }

    [Fact]
    public async Task ASessionNamedOnPurpose_KeepsItsName_AndSaysSo()
    {
        var (handle, session) = _HandleOnASession();
        Dispatcher.UIThread.Invoke(() =>
        {
            session.Title = "release work";
            session.HasGeneratedName = false;
        });

        var renamed = await handle.SuggestNameAsync("AC-312");

        // False is the whole point: the agent is told the name stood, rather than believing it renamed anything.
        Assert.False(renamed);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("release work", session.Title));
    }

    private static (SessionPanelHandle Handle, SessionPanelViewModel Session) _HandleOnASession() =>
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new SessionViewModel();
            cockpit.Sessions.Add(session);

            return (new SessionPanelHandle(session, isEmbedded: false, () => null), (SessionPanelViewModel)session);
        });
}
