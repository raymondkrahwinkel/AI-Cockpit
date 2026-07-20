using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-128: attach_message_images_to_issue keys on the transport-verified caller pane, not the agent-declared
/// <c>session</c>, so an agent cannot read another session's current-turn images by naming its id (confused deputy)
/// and upload them to an issue.
/// </summary>
public class YouTrackAttachToolsTests
{
    [Fact]
    public async Task AttachMessageImages_KeysOnTheVerifiedCallerPane_NotTheAgentSuppliedSession()
    {
        var sessions = Substitute.For<ICockpitSessionObserver>();
        sessions.GetCurrentTurnImages(Arg.Any<string>()).Returns([]); // no images -> the tool returns before uploading
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("verified-pane");
        host.Sessions.Returns(sessions);
        var tools = new YouTrackAttachTools(host, new YouTrackSettings(Substitute.For<IPluginStorage>()));

        // The agent spoofs another session's id in the tool argument.
        await tools.AttachMessageImagesToIssue("AC-1", session: "victim-pane");

        // The images are read for the verified caller, never the spoofed id.
        sessions.Received().GetCurrentTurnImages("verified-pane");
        sessions.DidNotReceive().GetCurrentTurnImages("victim-pane");
    }
}
