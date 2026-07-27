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
public class ScreenshotInjectionTests : IDisposable
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
    /// A pty carries bytes and no byte sequence means "here is an image" — but the agent running in the terminal
    /// reads a path perfectly well (AC-341). So the capture is written where it can be read and the path is pasted
    /// into the prompt: no clipboard, and nothing the operator had copied is destroyed to make room for it.
    /// </summary>
    [Fact]
    public async Task ATerminalSession_PastesThePathOfTheFileItWroteTheCaptureTo()
    {
        var session = _CreateTtySession();
        var pasted = new List<string>();
        session.PasteTextAsync = text => { pasted.Add(text); return Task.CompletedTask; };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().BeNull();
        var path = pasted.Should().ContainSingle().Which;
        File.Exists(path).Should().BeTrue("the agent reads the file the path points at, so it has to be there when the path arrives");
        (await File.ReadAllBytesAsync(path)).Should().Equal(Png, "what the agent reads has to be the capture the operator confirmed");
    }

    /// <summary>
    /// A capture that could not be written down must not be followed by a paste: the agent would go looking for a
    /// file that is not there and answer about a missing path — an error about the wrong thing, and one the
    /// operator cannot act on.
    /// </summary>
    [Fact]
    public async Task ATerminalSession_ThatCannotWriteTheCapture_PastesNothing_AndSaysWhy()
    {
        // A file where the directory has to go: creating it fails, which is what a full or read-only temp
        // directory does to this path as well.
        await File.WriteAllTextAsync(_spillDirectory, "not a directory");
        var session = _CreateTtySession();
        var pasted = new List<string>();
        session.PasteTextAsync = text => { pasted.Add(text); return Task.CompletedTask; };

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
        pasted.Should().BeEmpty("pasting a path to a file that was never written is worse than saying so");
    }

    /// <summary>
    /// A capture that lands after the operator closed the session must not report success into nothing. Closing a
    /// panel removes it from the collection and takes its container out of the visual tree without the view's own
    /// DataContext hook ever firing, so the panel has to let go of the paste itself when it is disposed.
    /// </summary>
    [Fact]
    public async Task AfterTheSessionIsClosed_ACaptureIsRefused_RatherThanReportedAsPasted()
    {
        var session = _CreateTtySession();
        var pasted = new List<string>();
        session.PasteTextAsync = text => { pasted.Add(text); return Task.CompletedTask; };

        await session.DisposeAsync();
        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull("the session is gone, and silence is what this whole path exists to prevent");
        pasted.Should().BeEmpty();
    }

    /// <summary>
    /// The paste is awaited, not merely started. The caller releases its one-capture-at-a-time guard on this task,
    /// so returning before the paste has happened would let two captures race into the same prompt.
    /// </summary>
    [Fact]
    public async Task TheInjection_WaitsForThePasteToActuallyHappen()
    {
        var session = _CreateTtySession();
        var pasteStarted = new TaskCompletionSource();
        var releasePaste = new TaskCompletionSource();
        session.PasteTextAsync = async _ =>
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

    /// <summary>
    /// Captures old enough that nothing can still be waiting on them are cleared out when the next one is
    /// written. A screenshot is precisely what this surface hands the operator a redaction tool for, so keeping
    /// every one of them in a shared temp directory forever is a decision, not an absence of one.
    /// </summary>
    [Fact]
    public async Task WritingACapture_ClearsOutOnesNothingCanStillBeWaitingOn()
    {
        Directory.CreateDirectory(_spillDirectory);
        var spent = Path.Combine(_spillDirectory, "screenshot-spent.png");
        var recent = Path.Combine(_spillDirectory, "screenshot-recent.png");
        await File.WriteAllBytesAsync(spent, Png);
        await File.WriteAllBytesAsync(recent, Png);
        File.SetLastWriteTimeUtc(spent, DateTime.UtcNow.AddDays(-2));
        var session = _CreateTtySession();
        session.PasteTextAsync = _ => Task.CompletedTask;

        await session.InjectScreenshotAsync(Png);

        File.Exists(spent).Should().BeFalse("two days is long past any prompt the operator was still typing");
        File.Exists(recent).Should().BeTrue("an agent may not have got round to reading this one yet");
    }

    /// <summary>
    /// A session with no view on it has nothing to paste into — a design-time graph, or a panel whose container
    /// left the tree. It says so, and writes no file it would only leave lying about.
    /// </summary>
    [Fact]
    public async Task ATerminalSessionThatIsNotOnScreen_SaysSo_AndWritesNothing()
    {
        var session = _CreateTtySession();

        var reason = await session.InjectScreenshotAsync(Png);

        reason.Should().NotBeNull();
        Directory.Exists(_spillDirectory).Should().BeFalse("a capture with nowhere to go is not worth spilling");
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

    private TtyViewModel _CreateTtySession()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());

        return new TtyViewModel(Substitute.For<ITtyLauncher>(), resolver) { SpillDirectory = _spillDirectory };
    }

    /// <summary>A spill directory of this test's own, so a run leaves nothing in the operator's temp directory and two tests cannot see each other's files.</summary>
    private readonly string _spillDirectory = Path.Combine(Path.GetTempPath(), $"cockpit-screenshot-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_spillDirectory))
        {
            Directory.Delete(_spillDirectory, recursive: true);
        }
        else if (File.Exists(_spillDirectory))
        {
            File.Delete(_spillDirectory);
        }
    }
}
