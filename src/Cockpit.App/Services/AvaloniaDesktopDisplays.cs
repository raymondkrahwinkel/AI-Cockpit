using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.Services;

// AC-1013: reports the desktop's displays through Avalonia for the Linux screenshot capture (AC-326), which
// gets one composed image from the portal and no word about what went into it; reads are marshalled to the UI
// thread. Under XWayland, KDE bug 502390 can double reported resolutions, so the capture reconciles against the image.
internal sealed class AvaloniaDesktopDisplays : IDesktopDisplays, ISingletonService
{
    // AC-1013: AC-577, deliberately no CheckAccess() fast path — the only caller always asks from a background
    // task, so a fast path would only mask a missing dispatcher loop as a considered "no displays" answer.
    // This type must not be constructed without a dispatcher loop, or this call hangs instead of failing.
    public Task<IReadOnlyList<DesktopDisplay>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(_Read).GetTask().WaitAsync(cancellationToken);

    private static IReadOnlyList<DesktopDisplay> _Read()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            // No window yet, or a design-time graph. The capture turns an empty list into a refusal naming what
            // is missing, which beats this inventing a screen nobody has.
            return [];
        }

        return window.Screens.All
            .Select(screen => new DesktopDisplay
            {
                Bounds = new CaptureRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
                Scale = screen.Scaling,
            })
            .ToList();
    }
}
