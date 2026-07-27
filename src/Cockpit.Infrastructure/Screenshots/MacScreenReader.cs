using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Reads the macOS desktop (AC-328): the displays from CoreGraphics, and their pixels from
/// <c>/usr/sbin/screencapture</c>, one display at a time.
/// </summary>
/// <remarks>
/// Not ScreenCaptureKit. That framework is for a stream of frames and needs Objective-C interop for a still the
/// system binary already writes to a path. Shelling out also keeps the "the capture is finished" signal clean:
/// the process exits.
/// <para>
/// There is no Mac here to run this against, so — as with <c>MacScreenLockMonitor</c> — the interop is kept thin
/// and written to the standard P/Invoke pattern, and everything that decides anything lives above the seam where
/// it is tested. What that leaves unverified is stated rather than glossed: <c>CGGetActiveDisplayList</c>'s
/// ordering matching <c>screencapture -D</c>'s numbering is the assumption this rests on.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacScreenReader(ILogger<MacScreenReader> logger) : IMacScreenReader
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ScreenCapture = "/usr/sbin/screencapture";

    /// <summary>More displays than anyone attaches, and a fixed ceiling is what the CoreGraphics call wants.</summary>
    private const int MaxDisplays = 16;

    public IReadOnlyList<MacDisplay> ReadDisplays()
    {
        var ids = new uint[MaxDisplays];
        if (CGGetActiveDisplayList(MaxDisplays, ids, out var count) != 0)
        {
            throw new InvalidOperationException("macOS would not say which displays are active.");
        }

        return Enumerable.Range(0, (int)count)
            .Select(position => _Describe(ids[position], position))
            .ToList();
    }

    public async Task<byte[]?> CaptureDisplayAsync(int displayIndex, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cockpit-screenshot-{Guid.NewGuid():n}.png");
        try
        {
            var start = new ProcessStartInfo(ScreenCapture)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };

            foreach (var argument in ScreenCaptureArguments.ForDisplay(displayIndex, path))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {ScreenCapture}.");

            // Disposing a Process object does not stop the process it describes, so without this a shutdown
            // mid-capture leaves screencapture running with nothing left to wait for it.
            await using var killOnCancel = cancellationToken.Register(() => _TryKill(process));

            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"screencapture exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(error) ? "no output" : error.Trim())}");
            }

            // No file, or an empty one. Without Screen Recording permission screencapture runs and writes
            // nothing, and it exits 0 doing so — so this is where "not allowed yet" arrives, looking like
            // "nothing to capture". The caller says which it thinks it was.
            var file = new FileInfo(path);
            return file is { Exists: true, Length: > 0 }
                ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
        }
        finally
        {
            _Discard(path);
        }
    }

    private static MacDisplay _Describe(uint id, int position) =>
        new()
        {
            // screencapture numbers displays from one, in the order CGGetActiveDisplayList reports them.
            Index = position + 1,
            Bounds = _BoundsOf(id),
            PixelWidth = (int)CGDisplayPixelsWide(id),
            PixelHeight = (int)CGDisplayPixelsHigh(id),
        };

    private static CaptureRect _BoundsOf(uint id)
    {
        var bounds = CGDisplayBounds(id);

        // Points, and fractional in principle: a rectangle that is not whole is rounded outwards so the display
        // keeps every point it covers rather than losing a sliver at its edge to a cast.
        var left = (int)Math.Floor(bounds.Origin.X);
        var top = (int)Math.Floor(bounds.Origin.Y);

        return new CaptureRect(
            left,
            top,
            (int)Math.Ceiling(bounds.Origin.X + bounds.Size.Width) - left,
            (int)Math.Ceiling(bounds.Origin.Y + bounds.Size.Height) - top);
    }

    /// <summary>Ends a capture nobody is waiting for any more. A process that has already exited is the ordinary case, not a failure.</summary>
    private void _TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not stop screencapture after the capture was cancelled.");
        }
    }

    /// <summary>Removes the file screencapture was told to write, whether or not it got that far — a picture of the operator's screen is not something to leave in the temp directory.</summary>
    private void _Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove the temporary screenshot file at {Path}.", path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);

    [DllImport(CoreGraphics)]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsHigh(uint display);
}
