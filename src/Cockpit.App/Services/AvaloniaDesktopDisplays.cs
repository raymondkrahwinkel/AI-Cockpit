using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.Services;

// Reports the desktop's displays through Avalonia for the Linux screenshot capture (AC-326), which gets one
// composed image from the portal and no word about what went into it. Lives here rather than in Infrastructure
// because Avalonia's screen list hangs off a window, and the only window is this app's.
// Every read is marshalled to the UI thread — the capture asks from a background task, and the screen list is
// the windowing system's, read on the thread that owns it.
//
// Avalonia's bounds are already in the desktop's own coordinates, which is the space the capture contract calls
// `CapturedDisplay.DesktopBounds`, so nothing is converted here. Under XWayland those numbers come
// through XRandR, which KDE bug 502390 has reporting doubled resolutions on some fractionally-scaled
// multi-monitor setups — the reason the capture reconciles them against the image it got rather than trusting
// them outright.
internal sealed class AvaloniaDesktopDisplays : IDesktopDisplays, ISingletonService
{
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
