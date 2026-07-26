using System.Buffers.Binary;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Portal;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The Linux capture with the D-Bus round-trips swapped out (AC-326): what is asked of the portal, and what is
/// made of the answer. The portal itself needs a live compositor and is verified by hand (AC-332); everything
/// this side of it — the option that decides whether a dialog opens, the layout reconciliation, the temp file —
/// is the part that can be held to a test, and the part that leaves a picture of the operator's screen on disk
/// when it goes wrong.
/// </summary>
public class PortalScreenshotCaptureTests : IDisposable
{
    private readonly string _tempDir;

    public PortalScreenshotCaptureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// The whole of AC-220's replacement in one option. <c>true</c> hands the operator their desktop's own
    /// screenshot dialog — a KDE form with three clicks in it — which is the UI this tool exists to own. Red the
    /// moment anyone flips it back.
    /// </summary>
    [Fact]
    public async Task ThePortalIsAskedNotToShowItsOwnUi()
    {
        IDictionary<string, object>? asked = null;
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromResult(2u),
            (options, _) =>
            {
                asked = options;
                return Task.FromResult(_Wrote(_Png(1920, 1080)));
            });

        await capture.CaptureAsync();

        asked.Should().ContainKey("interactive").WhoseValue.Should().Be(false);
    }

    /// <summary>A desktop with no screenshot portal has to be known before the operator presses anything, not after — the button reads this to disable itself with a reason.</summary>
    [Fact]
    public async Task ADesktopWithoutTheInterface_IsNotSupported()
    {
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromException<uint>(new InvalidOperationException("org.freedesktop.DBus.Error.ServiceUnknown")),
            (_, _) => Task.FromResult(_Wrote(_Png(1920, 1080))));

        await capture.ProbeVersionForTestAsync();

        capture.IsSupported.Should().BeFalse();
    }

    [Fact]
    public async Task ADesktopServingTheInterface_IsSupported()
    {
        var capture = _Capture();
        capture.UseTestHarness(_ => Task.FromResult(2u), (_, _) => Task.FromResult(_Wrote(_Png(1920, 1080))));

        await capture.ProbeVersionForTestAsync();

        capture.IsSupported.Should().BeTrue();
    }

    /// <summary>
    /// The probe is a round trip to the desktop, and the cockpit wires the composer's button in the same
    /// statement that builds this. So the wait has to be observable: a capture that claimed to be settled while
    /// still asking would leave that button greyed out for the rest of the run on a desktop that can capture
    /// perfectly well.
    /// </summary>
    [Fact]
    public async Task WhileTheDesktopIsStillBeingAsked_SupportIsNotSettled()
    {
        var answered = new TaskCompletionSource<uint>();
        var capture = _Capture();
        capture.UseTestHarness(_ => answered.Task, (_, _) => Task.FromResult(_Wrote(_Png(1920, 1080))));
        var probe = capture.ProbeVersionForTestAsync();

        capture.SupportSettled.IsCompleted.Should().BeFalse();
        capture.IsSupported.Should().BeFalse();

        answered.SetResult(2u);
        await probe;

        capture.SupportSettled.IsCompleted.Should().BeTrue();
        capture.IsSupported.Should().BeTrue();
    }

    /// <summary>Until the probe has answered, the honest answer is "no" — the alternative is a live button on a desktop that turns out to have no portal.</summary>
    [Fact]
    public void BeforeTheProbeHasAnswered_NothingIsClaimed()
    {
        var capture = new PortalScreenshotCapture(
            new StubDesktopDisplays([]),
            NullLogger<PortalScreenshotCapture>.Instance);

        capture.IsSupported.Should().BeFalse();
    }

    /// <summary>The layout the selection UI crops by comes back with the pixels, placed against the image that actually arrived.</summary>
    [Fact]
    public async Task TheDesktopsDisplaysComeBackWithTheImage()
    {
        var capture = _Capture(_Displays((0, 0, 1920, 1080, 1.5)));
        capture.UseTestHarness(_ => Task.FromResult(2u), (_, _) => Task.FromResult(_Wrote(_Png(2880, 1620))));

        var result = await capture.CaptureAsync();

        result.Should().NotBeNull();
        result!.Displays.Should().ContainSingle();
        result.Displays[0].ImageBounds.Should().Be(new CaptureRect(0, 0, 2880, 1620));
        result.Displays[0].DesktopBounds.Should().Be(new CaptureRect(0, 0, 1920, 1080));
    }

    /// <summary>
    /// A display list that does not account for the image means one of the two describes a different desktop.
    /// Cropping by it would take the wrong region and look entirely normal doing it, so the capture fails
    /// loudly instead — the operator gets a toast, not a screenshot of somewhere else.
    /// </summary>
    [Fact]
    public async Task AnImageTheDisplaysDoNotAccountFor_IsRefusedRatherThanCropped()
    {
        var capture = _Capture(_Displays((0, 0, 1920, 1080, 1.0)));
        capture.UseTestHarness(_ => Task.FromResult(2u), (_, _) => Task.FromResult(_Wrote(_Png(3840, 1080))));

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*3840*1080*");
    }

    /// <summary>No displays, no mapping — and a capture that cannot be selected from is not one to hand on in silence.</summary>
    [Fact]
    public async Task ADesktopReportingNoDisplays_IsRefused()
    {
        var capture = _Capture(new StubDesktopDisplays([]));
        capture.UseTestHarness(_ => Task.FromResult(2u), (_, _) => Task.FromResult(_Wrote(_Png(1920, 1080))));

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no displays*");
    }

    /// <summary>Something other than the image came back. A size read out of bytes that are not a PNG would be a number, and a plausible one.</summary>
    [Fact]
    public async Task APortalFileThatIsNotAPng_IsRefused()
    {
        var capture = _Capture();
        capture.UseTestHarness(_ => Task.FromResult(2u), (_, _) => Task.FromResult(_Wrote("<html>bad gateway</html>"u8.ToArray())));

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a readable PNG*");
    }

    /// <summary>Consent declined. An ordinary answer, not a failure — the caller passes over it in silence, as it does a selection nobody completed.</summary>
    [Fact]
    public async Task ADeclinedRequest_CapturesNothing()
    {
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromResult(2u),
            (_, _) => Task.FromResult(new PortalResponse(1, new Dictionary<string, object>())));

        (await capture.CaptureAsync()).Should().BeNull();
    }

    /// <summary>A portal that answers with a code nobody asked for is broken, and broken is not cancelled — the operator pressed a key and is owed the difference.</summary>
    [Fact]
    public async Task APortalThatRefuses_Throws()
    {
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromResult(2u),
            (_, _) => Task.FromResult(new PortalResponse(2, new Dictionary<string, object>())));

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*response code 2*");
    }

    /// <summary>Success without a location is a portal that did not do what it said. Nothing to read, and nothing to invent.</summary>
    [Fact]
    public async Task ASuccessWithoutAUri_Throws()
    {
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromResult(2u),
            (_, _) => Task.FromResult(new PortalResponse(0, new Dictionary<string, object>())));

        var act = async () => await capture.CaptureAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*where the image is*");
    }

    /// <summary>The file the portal wrote is gone by the time the capture returns — a successful read is no reason to leave a picture of the operator's screen in a cache directory.</summary>
    [Fact]
    public async Task AfterASuccessfulCapture_ThePortalsFileIsGone()
    {
        var path = Path.Combine(_tempDir, "captured.png");
        await File.WriteAllBytesAsync(path, _Png(1920, 1080));
        var capture = _Capture();
        capture.UseTestHarness(
            _ => Task.FromResult(2u),
            (_, _) => Task.FromResult(new PortalResponse(0, new Dictionary<string, object> { ["uri"] = new Uri(path).AbsoluteUri })));

        await capture.CaptureAsync();

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task TheImageIsRead_AndTheFileIsGone()
    {
        var path = Path.Combine(_tempDir, "shot.png");
        var written = new byte[] { 0x89, 0x50, 0x4E, 0x47, 7, 7 };
        await File.WriteAllBytesAsync(path, written);

        var bytes = await _Capture().ReadAndDiscardForTestAsync(new Uri(path).AbsoluteUri, CancellationToken.None);

        bytes.Should().Equal(written);
        File.Exists(path).Should().BeFalse("nothing ever comes back for it, so leaving it is leaving a screenshot behind");
    }

    /// <summary>
    /// The read failing is exactly when the file must still go: it is a picture of whatever was on their screen,
    /// and the failure that left it there is also the reason nobody would think to look for it. Red without the
    /// <c>finally</c> — with the delete sitting after the read, a cancelled read leaves the file on disk.
    /// </summary>
    [Fact]
    public async Task AReadThatIsCancelled_StillRemovesTheFile()
    {
        var path = Path.Combine(_tempDir, "cancelled.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await _Capture().ReadAndDiscardForTestAsync(new Uri(path).AbsoluteUri, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(path).Should().BeFalse("a failed read is no reason to leave a screenshot of the operator's screen lying about");
    }

    /// <summary>A portal that answers with something other than a file is a broken portal, not a capture to guess at — and nothing is deleted on a path we did not understand.</summary>
    [Fact]
    public async Task ALocationThatIsNotAFile_IsRefused()
    {
        var act = async () => await _Capture().ReadAndDiscardForTestAsync("https://example.invalid/shot.png", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private PortalScreenshotCapture _Capture(IDesktopDisplays? displays = null) =>
        new(displays ?? _Displays((0, 0, 1920, 1080, 1.0)), NullLogger<PortalScreenshotCapture>.Instance);

    private static StubDesktopDisplays _Displays(params (int X, int Y, int Width, int Height, double Scale)[] displays) =>
        new(displays
            .Select(display => new DesktopDisplay
            {
                Bounds = new CaptureRect(display.X, display.Y, display.Width, display.Height),
                Scale = display.Scale,
            })
            .ToList());

    /// <summary>Writes the bytes where a portal would have and answers with its location, so the file half of the capture runs for real.</summary>
    private PortalResponse _Wrote(byte[] image)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():n}.png");
        File.WriteAllBytes(path, image);

        return new PortalResponse(0, new Dictionary<string, object> { ["uri"] = new Uri(path).AbsoluteUri });
    }

    /// <summary>A PNG header and nothing behind it — the capture reads the dimensions and never decodes a pixel.</summary>
    private static byte[] _Png(int width, int height)
    {
        var png = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);

        return png;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
