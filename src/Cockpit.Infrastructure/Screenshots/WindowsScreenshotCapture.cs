using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on Windows through the <c>ms-screenclip:</c> protocol (AC-220) — the native Snip overlay
/// the operator already knows from Win+Shift+S, with its region, window, full-screen and freeform modes. The
/// ticket's alternative, a selection overlay of our own, would mean reimplementing all four plus the
/// screen-pixel reads behind them; this borrows the one Windows ships.
/// </summary>
/// <remarks>
/// What it costs is the return path. The protocol launch is fire-and-forget: it reports neither completion nor
/// cancellation, and the snip lands on the clipboard rather than coming back to the caller. So this watches the
/// clipboard for an image that was not there before and gives up after <see cref="_timeout"/>. Two consequences
/// worth stating rather than hiding:
/// <list type="bullet">
/// <item>A cancelled snip and a snip the operator never got round to look identical — both end as "nothing captured".</item>
/// <item>The snip replaces whatever was on the clipboard. That is Snip's doing, not ours, and the same thing happens on Win+Shift+S; nothing here clears or restores it.</item>
/// <item>
/// Any <em>other</em> image copied while the overlay is open is taken for the snip — the clipboard is all this can
/// see, and a new image on it is the only signal there is. It needs the operator to copy a second picture in the
/// seconds they are dragging a region, so it is unlikely rather than impossible; the honest bound is that this
/// watches the clipboard, not the overlay.
/// </item>
/// <item>
/// The mirror of that: a snip byte-for-byte identical to what was already on the clipboard — the same static
/// region grabbed twice in a row — is indistinguishable from nothing having happened, and ends as a cancel.
/// </item>
/// </list>
/// <para>
/// Interim against <see cref="IScreenshotCapture"/> (AC-333): the contract asks for every display and no UI, and
/// this still launches Snip. What lands on the clipboard is whatever the operator chose, with no layout that
/// could honestly be put on it — hence <see cref="ScreenCapture.WithoutLayout"/>. AC-327 replaces the whole
/// route with a DXGI/BitBlt read, which both removes the overlay and makes the layout knowable.
/// </para>
/// </remarks>
internal sealed class WindowsScreenshotCapture(IScreenshotClipboard clipboard, ILogger<WindowsScreenshotCapture> logger) : IScreenshotCapture
{
    private TimeSpan _pollInterval = TimeSpan.FromMilliseconds(400);
    private TimeSpan _timeout = TimeSpan.FromMinutes(2);
    private Action _launchOverlay = _LaunchSnipOverlay;
    private Func<TimeSpan, CancellationToken, Task> _wait = Task.Delay;

    public bool IsSupported => true;

    /// <summary>Nothing to ask anyone: Windows ships the route this takes.</summary>
    public Task SupportSettled => Task.CompletedTask;

    /// <summary>
    /// Test seam: swap the protocol launch and the wait between polls, so the clipboard-watching loop — the part
    /// with the actual logic in it — is assertable without a Snip overlay or two minutes of real time.
    /// </summary>
    internal void UseTestHarness(Action launchOverlay, Func<TimeSpan, CancellationToken, Task> wait, TimeSpan pollInterval, TimeSpan timeout)
    {
        _launchOverlay = launchOverlay;
        _wait = wait;
        _pollInterval = pollInterval;
        _timeout = timeout;
    }

    public async Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        // Read first, launch second: whatever is on the clipboard now is what a new snip has to differ from.
        var before = await clipboard.TryReadImageAsync(cancellationToken).ConfigureAwait(false);

        _launchOverlay();

        // Both operands are settings, not arithmetic to trust: a zero interval divides by zero and an interval
        // longer than the timeout would yield no attempt at all, which is a capture that never even looks.
        var attempts = _pollInterval > TimeSpan.Zero ? Math.Max(1, (int)(_timeout.Ticks / _pollInterval.Ticks)) : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await _wait(_pollInterval, cancellationToken).ConfigureAwait(false);

            var current = await clipboard.TryReadImageAsync(cancellationToken).ConfigureAwait(false);
            if (current is { Length: > 0 } && !_IsSameImage(before, current))
            {
                return ScreenCapture.WithoutLayout(current);
            }
        }

        logger.LogInformation(
            "No snip reached the clipboard within {Timeout}; treating it as cancelled.", _timeout);

        return null;
    }

    /// <summary>
    /// Whether the clipboard still holds what it held before the overlay opened. Length first, because a new
    /// screenshot almost never encodes to the byte count of the old one — the full compare is the rare path.
    /// </summary>
    private static bool _IsSameImage(byte[]? before, byte[] current) =>
        before is not null && before.Length == current.Length && before.AsSpan().SequenceEqual(current);

    // UseShellExecute is what makes a protocol activation work at all: ms-screenclip: is a registered URI
    // handler, not an executable to start. The process handle it returns is the shell's, not the overlay's,
    // which is the other half of why completion has to be watched for on the clipboard.
    private static void _LaunchSnipOverlay() =>
        Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true })?.Dispose();
}
