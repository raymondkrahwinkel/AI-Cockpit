namespace Cockpit.Core.HeavyRuns;

public sealed class DiskSpaceForecast(string name)
{
    private readonly List<(DateTimeOffset Timestamp, long FreeBytes)> _samples = [];

    public DiskSpaceSnapshot Sample(DateTimeOffset timestamp, long totalBytes, long freeBytes, HeavyRunSettings settings)
    {
        // The extra hour preserves the start of growth windows ending within the last day.
        _samples.RemoveAll(sample => sample.Timestamp < timestamp.AddHours(-25));
        _samples.Add((timestamp, freeBytes));
        var recent = _samples.Where(sample => sample.Timestamp >= timestamp.AddMinutes(-30)).ToArray();
        var meanTime = recent.Average(sample => (sample.Timestamp - timestamp).TotalSeconds);
        var meanFree = recent.Average(sample => (double)sample.FreeBytes);
        double covariance = 0;
        double variance = 0;
        foreach (var sample in recent)
        {
            var delta = (sample.Timestamp - timestamp).TotalSeconds - meanTime;
            covariance += delta * (sample.FreeBytes - meanFree);
            variance += delta * delta;
        }

        var growthPerSecond = variance > 0 ? -covariance / variance : 0;
        var secondsToFull = growthPerSecond > 0 ? freeBytes / growthPerSecond : double.PositiveInfinity;
        TimeSpan? timeToFull = freeBytes == 0 ? TimeSpan.Zero
            : secondsToFull < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(secondsToFull) : null;

        long maxHourlyGrowth = 0;
        var start = 0;
        for (var end = 0; end < _samples.Count; end++)
        {
            while (_samples[start].Timestamp < _samples[end].Timestamp.AddHours(-1))
            {
                start++;
            }

            if (_samples[end].Timestamp >= timestamp.AddHours(-24))
            {
                maxHourlyGrowth = Math.Max(maxHourlyGrowth, _samples[start].FreeBytes - _samples[end].FreeBytes);
            }
        }

        var freeThreshold = Math.Max(totalBytes * settings.DiskMinimumFreePercent / 100,
            maxHourlyGrowth * settings.DiskHourlyGrowthMultiplier);
        var pressure = freeBytes < freeThreshold / 2 || timeToFull?.TotalHours < settings.DiskCriticalTimeToFullHours
            ? DiskPressure.Critical
            : freeBytes < freeThreshold || timeToFull?.TotalHours < settings.DiskElevatedTimeToFullHours
                ? DiskPressure.Elevated
                : DiskPressure.Normal;
        return new DiskSpaceSnapshot(name, totalBytes, freeBytes, timeToFull, pressure);
    }
}
