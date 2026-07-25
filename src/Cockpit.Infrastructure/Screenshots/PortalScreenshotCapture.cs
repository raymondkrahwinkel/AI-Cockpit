using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Portal;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on Linux through the XDG desktop portal's <c>org.freedesktop.portal.Screenshot</c>
/// interface (AC-220) — the same route push-to-talk takes for its global hotkey, and for the same reason:
/// under Wayland an application cannot read the screen itself, the compositor does it. Asking with
/// <c>interactive: true</c> hands the operator their own desktop's picker (Spectacle on KDE, the shell's
/// screenshot UI on GNOME), so dragging a region, picking a window and grabbing the whole screen are all
/// there without the cockpit implementing any of them.
/// </summary>
/// <remarks>
/// The connection is opened per capture rather than held: a screenshot is an occasional, operator-initiated
/// act, and a D-Bus connection kept open for the life of the app to serve it would outlive its usefulness by
/// hours. The hotkey service holds one because it has a subscription to keep alive; this has nothing to keep.
/// </remarks>
internal sealed class PortalScreenshotCapture(ILogger<PortalScreenshotCapture> logger) : IScreenshotCapture
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    public bool IsSupported => true;

    public async Task<byte[]?> CaptureInteractiveAsync(CancellationToken cancellationToken = default)
    {
        using var connection = new Connection(Address.Session);
        var requests = await PortalRequestChannel.ConnectAsync(connection).ConfigureAwait(false);
        var screenshot = connection.CreateProxy<IScreenshotPortal>(BusName, DesktopPath);

        var response = await requests.InvokeAsync(
            token => screenshot.ScreenshotAsync(
                // No parent window: the cockpit's own toplevel identifier would need the portal's window-export
                // handshake, and what it buys is the picker being modal to our window — which is the wrong
                // relationship for a screenshot of everything except our window.
                string.Empty,
                new Dictionary<string, object>
                {
                    ["handle_token"] = token,
                    ["interactive"] = true,
                }),
            cancellationToken).ConfigureAwait(false);

        if (response.IsCancelled)
        {
            return null;
        }

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException($"The screenshot portal refused the request (response code {response.ResponseCode}).");
        }

        if (response.Results.TryGetValue("uri", out var value) is false || value is not string uri)
        {
            throw new InvalidOperationException("The screenshot portal reported success without saying where the image is.");
        }

        return await _ReadAndDiscardAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: the file half of a capture, without the D-Bus round-trip in front of it. What the portal does
    /// needs a live compositor and is verified by hand; what happens to the file it wrote should not have to be.
    /// </summary>
    internal Task<byte[]> ReadAndDiscardForTestAsync(string uri, CancellationToken cancellationToken) =>
        _ReadAndDiscardAsync(uri, cancellationToken);

    /// <summary>
    /// Reads the image the portal wrote and removes the file. The portal hands back a path rather than bytes,
    /// and nothing else ever comes back for it — leaving them is a screenshot of the operator's screen sitting
    /// in a cache directory for every capture they ever take.
    /// </summary>
    /// <remarks>
    /// The removal is in a <c>finally</c>, not after the read. A read that throws or is cancelled is exactly the
    /// case where the file must still go: it is a picture of whatever was on their screen, and the failure that
    /// left it there is also the reason nobody would think to look for it.
    /// </remarks>
    private async Task<byte[]> _ReadAndDiscardAsync(string uri, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            throw new InvalidOperationException($"The screenshot portal returned a location that is not a file: '{uri}'.");
        }

        var path = parsed.LocalPath;
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _Discard(path);
        }
    }

    /// <summary>Removes the file the portal wrote, whether or not it could be read.</summary>
    private void _Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            // The capture's own outcome is already decided, so this cannot change it — but it is worth saying,
            // because the leftovers accumulate silently and a reader of the log is the only one who would notice.
            logger.LogWarning(exception, "Could not remove the temporary screenshot file at {Path}.", path);
        }
    }
}
