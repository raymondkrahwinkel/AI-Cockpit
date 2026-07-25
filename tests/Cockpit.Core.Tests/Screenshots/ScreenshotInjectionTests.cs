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
    /// itself when it sees a paste (AC-226). So the image goes on the clipboard and the terminal is asked to
    /// perform its own paste: exactly what an operator does by hand, which is how this route was found.
    /// </summary>
    [Fact]
    public async Task ATerminalSession_PutsItOnTheClipboard_AndAsksTheTerminalToPaste()
    {
        var clipboard = new FakeScreenshotClipboard();
        var session = _CreateTtySession(clipboard);
        var pastes = 0;
        session.PasteAsync = () => { pastes++; return Task.CompletedTask; };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().BeNull();
        clipboard.Written.Should().ContainSingle().Which.Should().Equal(Png);
        pastes.Should().Be(1, "the terminal's own paste is what makes the TUI read the clipboard");
    }

    /// <summary>
    /// On Windows the capture reads the image off the clipboard, so it is already there — and writing it back is
    /// not a harmless no-op. Measured on 2026-07-25: the round trip replaced what the OS had put there with our
    /// own re-encoding, and afterwards even a manual Ctrl+V no longer pasted. Worse than not working, because it
    /// destroys what the operator had on their clipboard.
    /// </summary>
    [Fact]
    public async Task AnImageTheClipboardAlreadyHolds_IsNotWrittenBack()
    {
        var clipboard = new FakeScreenshotClipboard { ReadResult = Png };
        var session = _CreateTtySession(clipboard);
        var pastes = 0;
        session.PasteAsync = () => { pastes++; return Task.CompletedTask; };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().BeNull();
        clipboard.Written.Should().BeEmpty("it is already on the clipboard; rewriting it is what broke it");
        pastes.Should().Be(1, "the paste is still what makes the TUI read it");
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
        var pastes = 0;
        session.PasteAsync = () => { pastes++; return Task.CompletedTask; };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
        reason.Should().Contain("clipboard");
        pastes.Should().Be(0, "asking the TUI to paste an image that is not there is worse than saying so");
    }

    /// <summary>
    /// A capture that lands after the operator closed the session must not report success into nothing. Closing a
    /// panel removes it from the collection and takes its container out of the visual tree without the view's own
    /// DataContext hook ever firing, so the panel has to let go of the paste itself when it is disposed.
    /// </summary>
    [Fact]
    public async Task AfterTheSessionIsClosed_ACaptureIsRefused_RatherThanReportedAsPasted()
    {
        var clipboard = new FakeScreenshotClipboard();
        var session = _CreateTtySession(clipboard);
        var pastes = 0;
        session.PasteAsync = () => { pastes++; return Task.CompletedTask; };

        await session.DisposeAsync();
        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull("the session is gone, and silence is what this whole path exists to prevent");
        pastes.Should().Be(0);
    }

    /// <summary>
    /// The paste is awaited, not merely started. The caller releases its one-capture-at-a-time guard on this task,
    /// so returning before the paste has happened lets a second capture overwrite the clipboard the first one is
    /// still waiting to read.
    /// </summary>
    [Fact]
    public async Task TheInjection_WaitsForThePasteToActuallyHappen()
    {
        var session = _CreateTtySession(new FakeScreenshotClipboard());
        var pasteStarted = new TaskCompletionSource();
        var releasePaste = new TaskCompletionSource();
        session.PasteAsync = async () =>
        {
            pasteStarted.SetResult();
            await releasePaste.Task;
        };

        var injection = session.InjectScreenshotAsync(Png);
        await pasteStarted.Task;

        injection.IsCompleted.Should().BeFalse("the paste is still running");
        releasePaste.SetResult();
        (await injection).Should().BeNull();
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
