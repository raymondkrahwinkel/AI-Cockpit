using Cockpit.App.Services;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// Presses the screenshot key a second time from inside the first capture — the operator hitting a hotkey twice
/// while the picker is still open. Holds the coordinator rather than closing over it, because the coordinator is
/// built <em>from</em> the capture that calls this and so does not exist yet when the closure is made.
/// </summary>
internal sealed class CaptureReentry
{
    public ScreenshotCoordinator? Coordinator { get; set; }

    public Task InvokeAsync() => Coordinator?.CaptureIntoSelectedSessionAsync() ?? Task.CompletedTask;
}
