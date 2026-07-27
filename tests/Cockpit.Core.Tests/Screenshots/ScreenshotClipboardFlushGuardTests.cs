using FluentAssertions;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// The capture write has to flush the clipboard, and this reads the source to say so (AC-341).
/// </summary>
/// <remarks>
/// A tripwire rather than a proof, and deliberately so: the behaviour it protects belongs to Avalonia's Win32
/// clipboard and no in-process test can reproduce it. Measured against a real system clipboard on 2026-07-27, a
/// <c>SetBitmapAsync</c> without a flush advertises <c>image/png, PNG, CF_DIB, CF_DIBV5, CF_BITMAP, Bitmap</c>
/// and hands back nothing for any of them — the set only promises the image, and <c>FlushAsync</c> is what makes
/// the OS render it. Avalonia's headless clipboard is a plain in-memory store, so a test written against it stays
/// green with the flush deleted; that was checked before falling back to this.
/// <para>
/// It exists because the flush reads like a redundant line — and this epic already lost a working route exactly
/// that way: AC-327 removed the branch a comment in <c>TtyViewModel</c> was warning about, and the screenshot
/// never reached a session again. A cleanup that takes this line out should have to answer for it.
/// </para>
/// <para>
/// Reading the source the way <c>ThemeHexColorGuardTests</c> does: which calls a method makes is not recoverable
/// from the compiled assembly without disassembling an async state machine.
/// </para>
/// </remarks>
public class ScreenshotClipboardFlushGuardTests
{
    [Fact]
    public void TheClipboardWrite_FlushesWhatItSets()
    {
        var source = _ClipboardWriterSource();

        var set = source.IndexOf("SetBitmapAsync", StringComparison.Ordinal);
        var flush = source.IndexOf("FlushAsync", StringComparison.Ordinal);

        set.Should().BeGreaterThan(-1,
            "the writer puts the image on the clipboard by setting a bitmap — if that call is gone this guard is " +
            "reading for something that no longer exists and would pass for the wrong reason");
        flush.Should().BeGreaterThan(-1,
            "an image that is set but never flushed is a promise Avalonia's Win32 clipboard renders for nobody: " +
            "the terminal finds nothing to paste, and so does a manual CTRL+V");
        flush.Should().BeGreaterThan(set, "there is nothing to flush before the image has been set");
    }

    /// <summary>
    /// The bitmap has to still be alive when the flush happens, because the flush is what forces the render.
    /// Hoisting the <c>using</c> out, or moving the flush past it, brings back a clipboard holding a disposed
    /// image — which fails the way this whole ticket did, silently.
    /// </summary>
    [Fact]
    public void TheImageIsStillAlive_WhenTheFlushHappens()
    {
        var source = _ClipboardWriterSource();

        var bitmap = source.IndexOf("using var bitmap", StringComparison.Ordinal);
        var flush = source.IndexOf("FlushAsync", StringComparison.Ordinal);
        var closesScope = source.IndexOf("catch (Exception)", StringComparison.Ordinal);

        bitmap.Should().BeGreaterThan(-1, "the bitmap is what gets set, so it has to be declared");
        closesScope.Should().BeGreaterThan(-1, "the try block the write lives in ends where the catch begins");
        flush.Should().BeInRange(bitmap, closesScope,
            "the flush belongs inside the scope that owns the bitmap — after it is disposed there is nothing left to render");
    }

    private static string _ClipboardWriterSource()
    {
        var source = _LocateRepositoryFolder("src")
            ?? throw new InvalidOperationException("No src/ directory above the test output — this test reads the repo it belongs to.");

        return File.ReadAllText(Path.Combine(source, "Cockpit.App", "Services", "AvaloniaScreenshotClipboard.cs"));
    }

    private static string? _LocateRepositoryFolder(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
