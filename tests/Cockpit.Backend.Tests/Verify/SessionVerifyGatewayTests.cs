using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Verify;
using NSubstitute;

namespace Cockpit.Backend.Tests.Verify;

/// <summary>
/// AC-1374: <c>SessionVerifyGateway</c> moved to Infrastructure with no direct unit test of its own before this —
/// only <c>VerifyMcpToolsTests</c> exercised it, through a fake <see cref="IVerifySessionGateway"/>. Grid panes
/// only, like the App implementation it replaces: an embedded pane (an Autopilot step) has no verify runner of
/// its own to hand a render back to.
/// </summary>
public class SessionVerifyGatewayTests
{
    [Fact]
    public async Task AGridPane_HandsBackItsWorkingDirectoryAndTakesTheRender()
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.WorkingDirectory.Returns("/repo");
        handle.IsEmbedded.Returns(false);
        handle.FeedVerifyResultAsync("caption", Arg.Any<byte[]>()).Returns(true);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns(handle);
        var gateway = new SessionVerifyGateway(sessions);

        Assert.Equal("/repo", gateway.GetWorkingDirectory("pane-a"));
        Assert.True(await gateway.FeedResultAsync("pane-a", "caption", []));
    }

    [Fact]
    public async Task AnEmbeddedPane_IsRefused()
    {
        var embedded = Substitute.For<ISessionHandle>();
        embedded.IsEmbedded.Returns(true);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns(embedded);
        var gateway = new SessionVerifyGateway(sessions);

        Assert.Null(gateway.GetWorkingDirectory("pane-a"));
        Assert.False(await gateway.FeedResultAsync("pane-a", "caption", []));
    }

    [Fact]
    public async Task AnUnknownPane_IsRefused()
    {
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-a").Returns((ISessionHandle?)null);
        var gateway = new SessionVerifyGateway(sessions);

        Assert.Null(gateway.GetWorkingDirectory("pane-a"));
        Assert.False(await gateway.FeedResultAsync("pane-a", "caption", []));
    }
}
