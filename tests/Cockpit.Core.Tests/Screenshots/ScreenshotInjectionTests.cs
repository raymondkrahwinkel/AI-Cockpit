using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The session kinds that cannot take a captured screenshot (AC-220), and that each of them says so. The
/// operator pressed a key to get this image; the one outcome that is not allowed is nothing happening with no
/// explanation, which is what the older <c>FeedVerifyResultAsync</c> path does — and may, since its caller is
/// an agent's tool call that already got the text snapshot another way.
/// </summary>
/// <remarks>
/// The branch that <em>does</em> attach lives in <c>ScreenshotAttachmentViewTests</c>: queuing an attachment
/// decodes a preview bitmap, which needs an Avalonia platform, which is a different test project.
/// </remarks>
public class ScreenshotInjectionTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    /// <summary>
    /// Ollama, LM Studio and today's plugin providers report <see cref="SessionCapabilities.SupportsVision"/>
    /// false — their driver never builds an image block, so an attachment queued there would leave with the next
    /// message and simply not be there.
    /// </summary>
    [Fact]
    public void ANonVisionSession_AttachesNothing_AndSaysWhy()
    {
        var session = _CreateSdkSession();
        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        var reason = session.InjectScreenshot(Png);

        reason.Should().NotBeNull();
        reason.Should().Contain("image input");
        session.PendingAttachments.Should().BeEmpty();
    }

    /// <summary>
    /// A pty carries bytes; there is no byte sequence that means "here is an image" to a program reading one.
    /// The verify path drops a screenshot here without a word, which is right for a tool call and wrong for a
    /// key the operator pressed.
    /// </summary>
    [Fact]
    public void ATerminalSession_TakesNothing_AndSaysWhy()
    {
        var session = _CreateTtySession();

        var reason = session.InjectScreenshot(Png);

        reason.Should().NotBeNull();
        reason.Should().Contain("terminal");
    }

    /// <summary>
    /// A capture that came back empty is a failure with no image to show for it, not a successful attachment of
    /// nothing — and an empty attachment chip would be exactly that.
    /// </summary>
    [Fact]
    public void AnEmptyCapture_IsReported_RatherThanAttached()
    {
        var session = _CreateSdkSession();

        var reason = session.InjectScreenshot([]);

        reason.Should().NotBeNull();
        session.PendingAttachments.Should().BeEmpty();
    }

    private static SessionViewModel _CreateSdkSession()
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());
        return new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()),
            Substitute.For<IVoicePushToTalkService>(),
            voiceSettingsStore);
    }

    private static TtyViewModel _CreateTtySession()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return new TtyViewModel(Substitute.For<ITtyLauncher>(), resolver);
    }
}
