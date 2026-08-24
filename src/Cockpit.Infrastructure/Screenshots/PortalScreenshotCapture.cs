using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Portal;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-326): Screen capture on Linux via the XDG portal's Screenshot interface, asked with
// `interactive: false` (AC-220's `interactive: true` cost three clicks through a per-desktop dialog).
// Trimmed: 148ms Plasma 6.7 measurement; layout reconciliation via `ComposedCaptureLayout`; per-capture D-Bus rationale.
internal sealed class PortalScreenshotCapture : IScreenshotCapture
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    private readonly IDesktopDisplays _displays;
    private readonly ILogger<PortalScreenshotCapture> _logger;

    private Func<CancellationToken, Task<uint>> _readVersion;
    private Func<IDictionary<string, object>, CancellationToken, Task<PortalResponse>> _request;

    // The interface's version, or 0 for a desktop that does not serve it. Started at construction rather than
    // on first use, because an operator has to know an unsupported desktop before they press anything — and
    // awaited by nobody, since `_ProbeVersionAsync` answers 0 rather than faulting.
    private Task<uint> _version;

    // A written-out constructor where the neighbours take a primary one: the probe has to be started here, and
    // C# will not let a field initializer call an instance method to do it.
    public PortalScreenshotCapture(IDesktopDisplays displays, ILogger<PortalScreenshotCapture> logger)
    {
        _displays = displays;
        _logger = logger;
        _readVersion = _ReadVersionOverDBusAsync;
        _request = _RequestOverDBusAsync;
        _version = _ProbeVersionAsync();
    }

    // Whether this desktop serves the screenshot portal at all. Was hardcoded `true` until AC-326, which
    // meant a desktop with no portal offered a live button and failed on the press. A probe still in flight
    // reads as "not supported" — the safe way round, and the reason `SupportSettled` exists.
    public bool IsSupported => _version is { IsCompletedSuccessfully: true, Result: > 0 };

    // The one platform where this is not already decided: the desktop has to be asked over D-Bus.
    public Task SupportSettled => _version;

    // Test seam: swap the two D-Bus round trips — the version read and the screenshot request — so the parts
    // with logic in them are assertable without a live compositor. What the portal itself does needs a desktop
    // and is verified by hand (AC-332); what is asked of it, and what is made of the answer, should not have to be.
    internal void UseTestHarness(
        Func<CancellationToken, Task<uint>> readVersion,
        Func<IDictionary<string, object>, CancellationToken, Task<PortalResponse>> request)
    {
        _readVersion = readVersion;
        _request = request;
    }

    // Test seam: re-runs the probe against whatever `UseTestHarness` installed, and hands back the
    // task so a test can wait for it instead of racing the one the constructor started against a bus that is
    // not there on the machine running the suite.
    internal Task<uint> ProbeVersionForTestAsync() => _version = _ProbeVersionAsync();

    public async Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var response = await _request(
            new Dictionary<string, object>
            {
                // False is the whole point of AC-220's replacement: true hands over the backend's own dialog,
                // which is the UI this tool exists to own. A test holds this to it.
                ["interactive"] = false,
            },
            cancellationToken).ConfigureAwait(false);

        if (response.IsCancelled)
        {
            // The consent prompt, declined. The first capture on a machine raises one; a capture after that does
            // not, so this is the operator saying no rather than anything being broken.
            _logger.LogInformation("The screenshot portal reported the request was cancelled — consent was declined.");
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

        var image = await _ReadAndDiscardAsync(uri, cancellationToken).ConfigureAwait(false);
        return new ScreenCapture
        {
            Image = image,
            Displays = await _LayoutOfAsync(image, cancellationToken).ConfigureAwait(false),
        };
    }

    // Test seam: the file half of a capture, without the D-Bus round-trip in front of it. What the portal does
    // needs a live compositor and is verified by hand; what happens to the file it wrote should not have to be.
    internal Task<byte[]> ReadAndDiscardForTestAsync(string uri, CancellationToken cancellationToken) =>
        _ReadAndDiscardAsync(uri, cancellationToken);

    // Places the desktop's displays into the image the portal wrote. Both halves have to be true at once — the
    // desktop must report displays, and the image must be the size they imply — because the selection UI crops
    // by these numbers and a wrong one crops the wrong region without anything looking amiss.
    private async Task<IReadOnlyList<CapturedDisplay>> _LayoutOfAsync(byte[] image, CancellationToken cancellationToken)
    {
        var displays = await _displays.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("The desktop reported no displays, so there is nothing to map the capture onto.");
        }

        if (!PngImage.TryReadSize(image, out var width, out var height))
        {
            throw new InvalidOperationException("The screenshot portal returned something that is not a readable PNG.");
        }

        return ComposedCaptureLayout.TryCompose(displays, width, height)
            ?? throw new InvalidOperationException(
                $"The capture is {width}×{height} pixels, which is not what the desktop's {displays.Count} display(s) add up to — refusing rather than cropping the wrong region.");
    }

    // Asks the portal for the interface version, and answers 0 for every way that can fail: a desktop with no
    // portal, a bus that is not there, a property the interface does not carry. All of them mean the same thing
    // to a caller — this machine cannot capture — and the distinction between them belongs in the log.
    private async Task<uint> _ProbeVersionAsync()
    {
        try
        {
            var version = await _readVersion(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("The desktop serves org.freedesktop.portal.Screenshot version {Version}.", version);
            return version;
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "This desktop does not serve the screenshot portal; screen capture is unavailable.");
            return 0;
        }
    }

    private async Task<uint> _ReadVersionOverDBusAsync(CancellationToken cancellationToken)
    {
        using var connection = new Connection(Address.Session);
        await connection.ConnectAsync().ConfigureAwait(false);

        return await connection
            .CreateProxy<IScreenshotPortal>(BusName, DesktopPath)
            .GetAsync<uint>("version")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PortalResponse> _RequestOverDBusAsync(IDictionary<string, object> options, CancellationToken cancellationToken)
    {
        using var connection = new Connection(Address.Session);
        var requests = await PortalRequestChannel.ConnectAsync(connection).ConfigureAwait(false);
        var screenshot = connection.CreateProxy<IScreenshotPortal>(BusName, DesktopPath);

        return await requests.InvokeAsync(
            token => screenshot.ScreenshotAsync(
                // No parent window: the cockpit's own toplevel identifier would need the portal's window-export
                // handshake, and what it buys is a dialog modal to our window — which is the wrong relationship
                // for a consent prompt about the whole screen.
                string.Empty,
                new Dictionary<string, object>(options) { ["handle_token"] = token }),
            cancellationToken).ConfigureAwait(false);
    }

    // AC-1013: Reads the image the portal wrote (a path, not bytes) and removes it in a `finally` — the failure
    // path is exactly when the file must still go, since leaving it means a screen capture sitting in a cache dir.
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

    // Removes the file the portal wrote, whether or not it could be read.
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
            _logger.LogWarning(exception, "Could not remove the temporary screenshot file at {Path}.", path);
        }
    }
}
