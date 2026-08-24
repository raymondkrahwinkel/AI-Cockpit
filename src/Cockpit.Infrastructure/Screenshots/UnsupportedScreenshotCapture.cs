using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// Fallback for a platform with none of the three capture routes wired up (AC-220), same shape as
// `NoOpGlobalHotkeyService`. Called via the hotkey path (no button to disable), it throws instead of returning
// null (AC-333): null means a cancelled selection, which the caller silently passes over — this is not that.
internal sealed class UnsupportedScreenshotCapture : IScreenshotCapture
{
    public bool IsSupported => false;

    // Settled by being here at all: this is the registration for a platform with no route to try.
    public Task SupportSettled => Task.CompletedTask;

    public Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Screen capture is not supported on this platform.");
}
