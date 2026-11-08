using Avalonia;
using Avalonia.Threading;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.App.Services;

// AC-2209: shows a running look-along three times over (the strip's cyan border with the time left and Stop, a frame
// round the watched display, and a tab on it with the last look and Stop), and ends it when the timer runs out or
// the strip is hidden. Where the OS cannot leave them out of a capture, it hides all of them just before a look.
internal sealed class ScreenLookAlongPresenter : ISingletonService
{
    private readonly ScreenLookAlong _lookAlong;
    private readonly CompanionWindowPresenter _companion;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LookAlongFrameWindow? _frame;
    private LookAlongTabWindow? _tab;
    private CompanionWindow? _strip;

    public ScreenLookAlongPresenter(ScreenLookAlong lookAlong, CompanionWindowPresenter companion)
    {
        _lookAlong = lookAlong;
        _companion = companion;
        _timer.Tick += (_, _) => _Refresh();
        lookAlong.Changed += (_, _) => Dispatcher.UIThread.Post(_Refresh);
        companion.Hidden += (_, _) => lookAlong.Stop();
        if (!OverlayWindows.CanExcludeFromCapture)
        {
            lookAlong.HideSignals = cancellationToken => Dispatcher.UIThread.InvokeAsync(() => _HideAsync(cancellationToken));
        }
    }

    private void _Refresh()
    {
        if (_lookAlong.Current is not { } session)
        {
            // The timer ran out without a call: end it for real, so a later look starts at the card.
            _lookAlong.Stop();
            _TearDown();

            return;
        }

        if (!_companion.IsVisible)
        {
            _companion.Show();
        }

        if (_companion.Window is { } strip && strip != _strip)
        {
            _strip?.ShowLookAlong(null);
            _strip = strip;
            strip.LookAlongStopRequested += _OnStop;
        }

        var left = session.Until - DateTimeOffset.UtcNow;
        _strip?.ShowLookAlong($"{(int)left.TotalMinutes}:{Math.Max(0, left.Seconds):00}");

        var display = new PixelRect(session.Display.X, session.Display.Y, session.Display.Width, session.Display.Height);
        if (_frame is null)
        {
            _frame = new LookAlongFrameWindow();
            _frame.Cover(display);
            _frame.Show();
            _tab = new LookAlongTabWindow();
            _tab.StopRequested += _OnStop;
        }

        _tab!.ShowLastLook(session.LastLook is { } at ? $"Last look {at.ToLocalTime():HH:mm}" : "No look yet");
        if (!_tab.IsVisible)
        {
            _tab.Show();
        }

        var size = PixelSize.FromSize(_tab.Bounds.Size, _tab.RenderScaling);
        _tab.Position = LookAlongTabWindow.Place(display, size, _strip?.OccupiedBounds() ?? []);
        _timer.Start();
    }

    private void _OnStop(object? sender, EventArgs e) => _lookAlong.Stop();

    private void _TearDown()
    {
        _timer.Stop();
        _strip?.ShowLookAlong(null);
        _frame?.Close();
        _frame = null;
        _tab?.Close();
        _tab = null;
    }

    private async Task<Action> _HideAsync(CancellationToken cancellationToken)
    {
        var restoreStrip = _strip is { IsVisible: true } strip ? strip.HideForCapture() : null;
        var hidden = new Avalonia.Controls.Window?[] { _frame, _tab }.Where(window => window is { IsVisible: true }).ToArray();
        foreach (var window in hidden)
        {
            window!.Hide();
        }

        // Let the compositor paint the desktop without them, as the screenshot button does.
        await Task.Delay(100, cancellationToken).ConfigureAwait(true);

        return () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var window in hidden.Where(window => window == _frame || window == _tab))
            {
                window!.Show();
            }

            restoreStrip?.Invoke();
        });
    }
}
