using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The half of the Linux capture that is reachable without a live desktop portal: what happens to the file the
/// portal wrote. The D-Bus round-trip itself needs a compositor and is out of unit-test reach, so it is verified
/// by hand (AC-227); this is the part that can be held to a test — and it is the part that leaves a picture of
/// the operator's screen on disk when it goes wrong.
/// </summary>
public class PortalScreenshotCaptureTests : IDisposable
{
    private readonly string _tempDir;

    public PortalScreenshotCaptureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    private static PortalScreenshotCapture _Capture() => new(NullLogger<PortalScreenshotCapture>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
