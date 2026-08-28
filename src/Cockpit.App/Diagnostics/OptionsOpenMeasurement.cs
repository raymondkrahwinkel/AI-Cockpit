using System.Diagnostics;

namespace Cockpit.App.Diagnostics;

internal sealed class OptionsOpenMeasurement(Func<long>? timestamp = null)
{
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private readonly long _startedAt = (timestamp ?? Stopwatch.GetTimestamp)();
    private readonly List<(string Phase, TimeSpan Elapsed)> _phases = [];

    public void Mark(string phase) => _phases.Add((phase, Stopwatch.GetElapsedTime(_startedAt, _timestamp())));

    public string? Finish()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startedAt, _timestamp());
        return elapsed < TimeSpan.FromMilliseconds(850)
            ? null
            : $"options open slow total={elapsed.TotalMilliseconds:0}ms phases={string.Join(',', _phases.Select(phase => $"{phase.Phase}:{phase.Elapsed.TotalMilliseconds:0}"))}";
    }
}
