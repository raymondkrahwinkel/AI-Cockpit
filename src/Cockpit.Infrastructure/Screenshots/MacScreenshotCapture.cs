using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on macOS through <c>screencapture -i</c> (AC-220) — the same interactive selection the
/// system-wide Cmd+Shift+4 offers: drag a region, or press Space to pick a window. It writes a PNG to the
/// path it is given, so unlike the Windows route there is a clear "the operator is done" signal: the process
/// exits.
/// </summary>
/// <remarks>
/// Cancelling (Escape) is not reported through the exit code — <c>screencapture</c> has exited 0 on a
/// cancelled selection across macOS versions. What it reliably does not do is write the file, so that is what
/// this reads: a file that is missing or empty means nothing was captured.
/// <para>
/// The first capture on a fresh machine raises the Screen Recording privacy prompt. Until the operator grants
/// it, macOS lets the capture run and yields nothing — indistinguishable here from a cancel, which is why the
/// hint the caller shows says so rather than claiming the picker was dismissed.
/// </para>
/// <para>
/// Interim against <see cref="IScreenshotCapture"/> (AC-333): the contract asks for every display and no UI, and
/// <c>-i</c> is exactly a UI. What it writes is whatever the operator selected, with no layout that could
/// honestly be put on it — hence <see cref="ScreenCapture.WithoutLayout"/>. AC-328 drops the <c>-i</c>, which is
/// what makes the layout knowable.
/// </para>
/// </remarks>
internal sealed class MacScreenshotCapture(ILogger<MacScreenshotCapture> logger) : IScreenshotCapture
{
    public bool IsSupported => true;

    /// <summary>Nothing to ask anyone: <c>screencapture</c> is part of macOS.</summary>
    public Task SupportSettled => Task.CompletedTask;

    public async Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cockpit-screenshot-{Guid.NewGuid():n}.png");

        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture")
            {
                // -i is the interactive selection; -x silences the shutter sound, which is a camera noise
                // nobody asked for when the point is to hand an image to an agent.
                ArgumentList = { "-i", "-x", path },
                UseShellExecute = false,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Could not start /usr/sbin/screencapture.");

            // Cancelling has to take the picker with it. Disposing the Process object does not stop the process
            // it describes, so a shutdown mid-selection would otherwise leave screencapture owning the operator's
            // screen with a crosshair on it and nothing left to dismiss it.
            await using var killOnCancel = cancellationToken.Register(() => _TryKill(process));

            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"screencapture exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(error) ? "no output" : error.Trim())}");
            }

            // No file, or an empty one: the selection was dismissed, or screen recording is not permitted yet.
            var file = new FileInfo(path);
            return file is { Exists: true, Length: > 0 }
                ? ScreenCapture.WithoutLayout(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
                : null;
        }
        finally
        {
            _Discard(path);
        }
    }

    /// <summary>Ends a picker nobody is waiting for any more. A process that has already exited is the ordinary case, not a failure.</summary>
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
            logger.LogWarning(exception, "Could not stop the screenshot picker after the capture was cancelled.");
        }
    }

    /// <summary>Removes the file screencapture was told to write, whether or not it got that far — a screenshot of the operator's screen is not something to leave in the temp directory.</summary>
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
}
