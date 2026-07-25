using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Fallback for a platform with none of the three capture routes wired up (AC-220). Says so through
/// <see cref="IsSupported"/> rather than throwing, so the screenshot button renders disabled with a reason
/// instead of offering something that cannot work — the same shape <c>NoOpGlobalHotkeyService</c> takes for
/// the global hotkey.
/// </summary>
internal sealed class UnsupportedScreenshotCapture(ILogger<UnsupportedScreenshotCapture> logger) : IScreenshotCapture
{
    public bool IsSupported => false;

    public Task<byte[]?> CaptureInteractiveAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Screen capture is not supported on this platform; nothing was captured.");
        return Task.FromResult<byte[]?>(null);
    }
}
