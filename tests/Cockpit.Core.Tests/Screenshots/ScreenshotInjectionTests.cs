using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;
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
    public async Task ANonVisionSession_AttachesNothing_AndSaysWhy()
    {
        var session = _CreateSdkSession();
        session.Capabilities = SessionCapabilities.ClaudeCli with { SupportsVision = false };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
        reason.Should().Contain("image input");
        session.PendingAttachments.Should().BeEmpty();
    }

    /// <summary>
    /// A pty carries bytes and no byte sequence means "here is an image" — but the TUI reads the system clipboard
    /// itself when it sees a paste key (AC-226). So the image goes on the clipboard and the single byte 0x16 goes
    /// down the pty: exactly what an operator does by hand, which is how this route was found.
    /// </summary>
    [Fact]
    public async Task ATerminalSession_PutsItOnTheClipboard_AndSendsThePasteKey()
    {
        var clipboard = new FakeScreenshotClipboard();
        var session = _CreateTtySession(clipboard);
        var toPty = new List<string>();
        session.VoiceTranscriptReady += toPty.Add;

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().BeNull();
        clipboard.Written.Should().ContainSingle().Which.Should().Equal(Png);
        // ContainSingle().Which rather than Equal(…, because): the params-string overload of Equal would take
        // the explanation for a second expected element.
        toPty.Should().ContainSingle("Ctrl+V is what makes the TUI go and read the clipboard")
            .Which.Should().Be("\u0016");
    }

    /// <summary>
    /// A clipboard that would not take the image must not be followed by the paste key: the TUI would go looking,
    /// find nothing, and answer with its own "no image in clipboard" — an error about the wrong thing, and one the
    /// operator cannot act on.
    /// </summary>
    [Fact]
    public async Task ATerminalSession_WhoseClipboardRefuses_SendsNoPasteKey_AndSaysWhy()
    {
        var clipboard = new FakeScreenshotClipboard { AcceptsWrites = false };
        var session = _CreateTtySession(clipboard);
        var toPty = new List<string>();
        session.VoiceTranscriptReady += toPty.Add;

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
        reason.Should().Contain("clipboard");
        toPty.Should().BeEmpty("asking the TUI to paste an image that is not there is worse than saying so");
    }

    /// <summary>Design-time and test graphs have no clipboard wired; that is a reason to report, not something to crash on.</summary>
    [Fact]
    public async Task ATerminalSessionWithNoClipboardWired_SaysSo()
    {
        var session = _CreateTtySession(clipboard: null);

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
    }

    /// <summary>
    /// A capture that came back empty is a failure with no image to show for it, not a successful attachment of
    /// nothing — and an empty attachment chip would be exactly that.
    /// </summary>
    [Fact]
    public async Task AnEmptyCapture_IsReported_RatherThanAttached()
    {
        var session = _CreateSdkSession();

        var reason = await session.InjectScreenshotAsync([]);

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

    private static TtyViewModel _CreateTtySession(IScreenshotClipboard? clipboard)
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return new TtyViewModel(Substitute.For<ITtyLauncher>(), resolver, screenshotClipboard: clipboard);
    }
}
