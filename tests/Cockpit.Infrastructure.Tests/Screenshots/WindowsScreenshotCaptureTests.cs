using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The Windows capture's clipboard-watching loop (AC-220). <c>ms-screenclip:</c> reports neither completion
/// nor cancellation and puts the snip on the clipboard, so "did anything happen?" is answered by watching for
/// an image that was not there before — which is the part with logic in it, and the part worth testing without
/// a Snip overlay or two minutes of real time.
/// </summary>
public class WindowsScreenshotCaptureTests
{
    private static readonly byte[] Snip = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly byte[] AlreadyCopied = [0x89, 0x50, 0x4E, 0x47, 9, 9];

    [Fact]
    public async Task AnImageAppearingOnTheClipboard_IsTheCapture()
    {
        var launches = 0;
        var capture = _Create(new ScriptedClipboard(null, null, Snip), () => launches++);

        var png = await capture.CaptureInteractiveAsync();

        png.Should().Equal(Snip);
        launches.Should().Be(1, "the overlay is opened once, then watched for");
    }

    /// <summary>
    /// Whatever the operator had copied before is not their screenshot. Without this the capture would return
    /// it instantly — a stale image attached to the session, and the picker still open.
    /// </summary>
    [Fact]
    public async Task AnImageThatWasAlreadyOnTheClipboard_IsNotMistakenForTheCapture()
    {
        var clipboard = new ScriptedClipboard(AlreadyCopied, AlreadyCopied, Snip);
        var capture = _Create(clipboard);

        var png = await capture.CaptureInteractiveAsync();

        png.Should().Equal(Snip);
    }

    /// <summary>A cancelled snip leaves the clipboard as it was, so the wait runs out — and running out is "nothing captured", not an error.</summary>
    [Fact]
    public async Task AClipboardThatNeverChanges_EndsAsNothingCaptured()
    {
        var clipboard = new ScriptedClipboard(AlreadyCopied, AlreadyCopied, AlreadyCopied, AlreadyCopied);
        var capture = _Create(clipboard);

        var png = await capture.CaptureInteractiveAsync();

        png.Should().BeNull();
    }

    [Fact]
    public async Task AnEmptyClipboardThroughout_EndsAsNothingCaptured()
    {
        var clipboard = new ScriptedClipboard(null, null, null, null);
        var capture = _Create(clipboard);

        var png = await capture.CaptureInteractiveAsync();

        png.Should().BeNull();
    }

    /// <summary>Giving up on the wait must not leave the operator's own capture behind — cancelling propagates rather than returning a quiet null.</summary>
    [Fact]
    public async Task Cancelling_StopsTheWait()
    {
        var clipboard = new ScriptedClipboard(null, null, null, null);
        var capture = new WindowsScreenshotCapture(clipboard, NullLogger<WindowsScreenshotCapture>.Instance);
        using var cancellation = new CancellationTokenSource();
        capture.UseTestHarness(
            launchOverlay: cancellation.Cancel,
            wait: (_, token) => Task.FromCanceled(token),
            pollInterval: TimeSpan.FromMilliseconds(1),
            timeout: TimeSpan.FromMilliseconds(10));

        var act = async () => await capture.CaptureInteractiveAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Wires the capture to a clipboard script, a launch that does nothing real, and a wait that does not wait.</summary>
    private static WindowsScreenshotCapture _Create(IScreenshotClipboard clipboard, Action? launchOverlay = null)
    {
        var capture = new WindowsScreenshotCapture(clipboard, NullLogger<WindowsScreenshotCapture>.Instance);
        capture.UseTestHarness(
            launchOverlay ?? (() => { }),
            wait: (_, _) => Task.CompletedTask,
            pollInterval: TimeSpan.FromMilliseconds(1),
            timeout: TimeSpan.FromMilliseconds(3));

        return capture;
    }
}
