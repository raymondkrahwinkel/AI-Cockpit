using System.Diagnostics;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Measures desktop stutter as UI-thread scheduling jitter (AC-68 slice 3): a background-priority dispatcher timer
/// that should tick every few milliseconds, and the amount by which a tick runs late is how long the UI thread was
/// busy or starved — the visible "hitch". It is a proxy, not a compositor frame-time (Avalonia exposes no public
/// hitch metric), and a completely idle desktop renders no frames to stutter — but during real work it captures the
/// main-thread contention a user actually sees, which is exactly what steers a GPU choice back to the CPU.
/// </summary>
internal sealed class UiHitchProbe : IUiHitchProbe, ISingletonService
{
    public IUiHitchSession Start() => new Session();

    private sealed class Session : IUiHitchSession
    {
        private const double IntervalMs = 8;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer;
        private double _lastMs;
        private double _maxHitchMs;

        public Session()
        {
            _lastMs = _stopwatch.Elapsed.TotalMilliseconds;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(IntervalMs), DispatcherPriority.Background, _OnTick);
            // The timer must live on the UI thread; the calibrator that starts us runs on a background thread.
            Dispatcher.UIThread.Post(_timer.Start);
        }

        // Read from the calibrator's thread; a torn double read is harmless for a proxy metric.
        public double MaxHitchMs => Volatile.Read(ref _maxHitchMs);

        private void _OnTick(object? sender, EventArgs e)
        {
            var now = _stopwatch.Elapsed.TotalMilliseconds;
            var overshoot = now - _lastMs - IntervalMs;
            _lastMs = now;
            if (overshoot > _maxHitchMs)
            {
                Volatile.Write(ref _maxHitchMs, overshoot);
            }
        }

        public void Dispose() => Dispatcher.UIThread.Post(_timer.Stop);
    }
}
