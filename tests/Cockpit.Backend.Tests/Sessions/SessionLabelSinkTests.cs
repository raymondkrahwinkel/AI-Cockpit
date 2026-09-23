using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1374: <c>SessionLabelSink</c> moved to Infrastructure and is now pure delegation — the UI-thread marshalling
/// that used to live here moved into the handle (<c>Cockpit.App.ViewTests.SessionLabelSinkTests</c> covers that
/// half, since it needs a real pane and a dispatcher). What is left is the pane-id lookup and its refusal.
/// </summary>
public class SessionLabelSinkTests
{
    [Fact]
    public async Task AKnownPane_DelegatesToItsHandle_AndReturnsWhatTheHandleSaid()
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.SetStatuslineAsync("AC-312").Returns(true);
        handle.SuggestNameAsync("AC-312").Returns(false);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns(handle);
        var sink = new SessionLabelSink(sessions);

        Assert.True(await sink.SetStatuslineAsync("pane-a", "AC-312"));
        Assert.False(await sink.SuggestNameAsync("pane-a", "AC-312"));
    }

    [Fact]
    public async Task APaneIdThatMatchesNothing_ReportsIt_WithoutTouchingAnyHandle()
    {
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("no-such-pane").Returns((ISessionHandle?)null);
        var sink = new SessionLabelSink(sessions);

        Assert.False(await sink.SetStatuslineAsync("no-such-pane", "AC-312"));
        Assert.False(await sink.SuggestNameAsync("no-such-pane", "AC-312"));
    }
}
