namespace Cockpit.Core.Voice;

// A fixed-width, scrolling history of microphone levels for the voice overlay's waveform: `Push` shifts
// the newest level in on the right, dropping the oldest, so bars read left-to-right as recent history.
// Pure and UI-free so the overlay view model stays a thin binding layer over it.
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

    // The current levels, oldest first — index 0 is the left edge, the last index the most recent sample.
    public IReadOnlyList<double> Levels => _levels;

    public void Push(double level)
    {
        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        _levels[^1] = Math.Clamp(level, 0, 1);
    }

    public void Reset() => Array.Clear(_levels);
}
