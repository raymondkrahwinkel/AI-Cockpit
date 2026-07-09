namespace Cockpit.Core.Voice;

/// <summary>
/// A fixed-width, scrolling history of microphone levels for the voice overlay's waveform: each
/// <see cref="Push"/> shifts the newest level in on the right and drops the oldest on the left, so the
/// bars read left-to-right as "what the mic heard over the last N frames". Pure and UI-free so the
/// overlay view model stays a thin binding layer over it.
/// </summary>
public sealed class WaveformLevelBuffer
{
    private readonly double[] _levels;

    public WaveformLevelBuffer(int barCount)
    {
        if (barCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(barCount), barCount, "A waveform needs at least one bar.");
        }

        _levels = new double[barCount];
    }

    public int BarCount => _levels.Length;

    /// <summary>The current levels, oldest first — index 0 is the left edge, the last index the most recent sample.</summary>
    public IReadOnlyList<double> Levels => _levels;

    public void Push(double level)
    {
        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        _levels[^1] = Math.Clamp(level, 0, 1);
    }

    public void Reset() => Array.Clear(_levels);
}
