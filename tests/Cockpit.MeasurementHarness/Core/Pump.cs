using Avalonia.Headless;

namespace Cockpit.MeasurementHarness.Core;

/// <summary>
/// Lets the app run for a while — by yielding the UI thread, never by sleeping on it. A harness that blocks
/// the thread it is measuring stops the render loop it is trying to count: the first version of this class
/// slept in one-millisecond steps and saw three frames in seven hundred milliseconds.
/// </summary>
public sealed class Pump(bool headless)
{
    /// <summary>Roughly one 120 Hz frame — short enough not to shape the cadence being measured.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(8);

    /// <summary>How many render ticks this run had to force. Belongs in the header: it says how the frames came about.</summary>
    public int ForcedRenderTicks { get; private set; }

    public bool Headless => headless;

    /// <summary>
    /// Waits, on the dispatcher, without holding it. Headless has no render timer of its own, so frames only
    /// happen when something asks for them — a run that forgets to is not a fast run but a blind one.
    /// </summary>
    public async Task ForAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            if (headless)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                ForcedRenderTicks++;
            }

            await Task.Delay(Step).ConfigureAwait(true);
        }
    }
}
