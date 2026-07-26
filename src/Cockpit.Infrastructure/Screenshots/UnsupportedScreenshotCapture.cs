using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Fallback for a platform with none of the three capture routes wired up (AC-220). Says so up front through
/// <see cref="IsSupported"/>, so the screenshot button renders disabled with a reason instead of offering
/// something that cannot work — the same shape <c>NoOpGlobalHotkeyService</c> takes for the global hotkey.
/// </summary>
/// <remarks>
/// Called anyway — the hotkey path has no button to disable — it throws rather than returning null (AC-333).
/// Null now means a read that produced no image, which the caller passes over in silence because that is what a
/// cancelled selection looks like; a platform that can never capture is not that, and an operator who pressed a
/// key is owed the difference.
/// </remarks>
internal sealed class UnsupportedScreenshotCapture : IScreenshotCapture
{
    public bool IsSupported => false;

    /// <summary>Settled by being here at all: this is the registration for a platform with no route to try.</summary>
    public Task SupportSettled => Task.CompletedTask;

    public Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Screen capture is not supported on this platform.");
}
