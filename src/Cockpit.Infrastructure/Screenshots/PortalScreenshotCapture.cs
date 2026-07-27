using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Portal;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on Linux through the XDG desktop portal's <c>org.freedesktop.portal.Screenshot</c>
/// interface (AC-326) — the same route push-to-talk takes for its global hotkey, and for the same reason:
/// under Wayland an application cannot read the screen itself, the compositor does it. Asked with
/// <c>interactive: false</c>, so what comes back is every display and no UI — the selection is the cockpit's
/// own (AC-329) and the portal is only where the pixels come from.
/// </summary>
/// <remarks>
/// AC-220 asked with <c>interactive: true</c> and got the backend's own dialog: on KDE a form with an Area
/// dropdown, a Delay spinner and a Take button, three clicks before anything was captured, and a different UI on
/// every desktop. Measured on Fedora 43 / Plasma 6.7 / Wayland, <c>interactive: false</c> prompts once for
/// consent and remembers it — 148 ms unattended afterwards, which is what a hotkey needs. The same call serves
/// X11, so one implementation covers both session types.
/// <para>
/// The portal says nothing about what went into the image it writes, so the layout the contract asks for comes
/// from the desktop separately (<see cref="IDesktopDisplays"/>) and has to be reconciled with the image
/// afterwards — <see cref="ComposedCaptureLayout"/> does that and refuses when the two disagree.
/// </para>
/// <para>
/// The connection is opened per capture rather than held: a screenshot is an occasional, operator-initiated
/// act, and a D-Bus connection kept open for the life of the app to serve it would outlive its usefulness by
/// hours. The hotkey service holds one because it has a subscription to keep alive; this has nothing to keep.
/// </para>
/// </remarks>
internal sealed class PortalScreenshotCapture : IScreenshotCapture
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    private readonly IDesktopDisplays _displays;
    private readonly ILogger<PortalScreenshotCapture> _logger;

    private Func<CancellationToken, Task<uint>> _readVersion;
    private Func<IDictionary<string, object>, CancellationToken, Task<PortalResponse>> _request;

    /// <summary>
    /// The interface's version, or 0 for a desktop that does not serve it. Started at construction rather than
    /// on first use, because an operator has to know an unsupported desktop before they press anything — and
    /// awaited by nobody, since <see cref="_ProbeVersionAsync"/> answers 0 rather than faulting.
    /// </summary>
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

    /// <summary>
    /// Whether this desktop serves the screenshot portal at all. Was hardcoded <c>true</c> until AC-326, which
    /// meant a desktop with no portal offered a live button and failed on the press. A probe still in flight
    /// reads as "not supported" — the safe way round, and the reason <see cref="SupportSettled"/> exists.
    /// </summary>
    public bool IsSupported => _version is { IsCompletedSuccessfully: true, Result: > 0 };

    /// <summary>The one platform where this is not already decided: the desktop has to be asked over D-Bus.</summary>
    public Task SupportSettled => _version;

    /// <summary>
    /// Test seam: swap the two D-Bus round trips — the version read and the screenshot request — so the parts
    /// with logic in them are assertable without a live compositor. What the portal itself does needs a desktop
    /// and is verified by hand (AC-332); what is asked of it, and what is made of the answer, should not have to be.
    /// </summary>
    internal void UseTestHarness(
        Func<CancellationToken, Task<uint>> readVersion,
        Func<IDictionary<string, object>, CancellationToken, Task<PortalResponse>> request)
    {
        _readVersion = readVersion;
        _request = request;
    }

    /// <summary>
    /// Test seam: re-runs the probe against whatever <see cref="UseTestHarness"/> installed, and hands back the
    /// task so a test can wait for it instead of racing the one the constructor started against a bus that is
    /// not there on the machine running the suite.
    /// </summary>
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

    /// <summary>
    /// Test seam: the file half of a capture, without the D-Bus round-trip in front of it. What the portal does
    /// needs a live compositor and is verified by hand; what happens to the file it wrote should not have to be.
    /// </summary>
    internal Task<byte[]> ReadAndDiscardForTestAsync(string uri, CancellationToken cancellationToken) =>
        _ReadAndDiscardAsync(uri, cancellationToken);

    /// <summary>
    /// Places the desktop's displays into the image the portal wrote. Both halves have to be true at once — the
    /// desktop must report displays, and the image must be the size they imply — because the selection UI crops
    /// by these numbers and a wrong one crops the wrong region without anything looking amiss.
    /// </summary>
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

    /// <summary>
    /// Asks the portal for the interface version, and answers 0 for every way that can fail: a desktop with no
    /// portal, a bus that is not there, a property the interface does not carry. All of them mean the same thing
    /// to a caller — this machine cannot capture — and the distinction between them belongs in the log.
    /// </summary>
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
            _logger.LogWarning(exception, "Could not remove the temporary screenshot file at {Path}.", path);
        }
    }
}
