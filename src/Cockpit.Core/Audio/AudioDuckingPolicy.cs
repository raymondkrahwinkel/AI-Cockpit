namespace Cockpit.Core.Audio;

public static class AudioDuckingPolicy
{
    public static TimeSpan ReleaseDelay => TimeSpan.FromMilliseconds(500);

    public static bool ShouldStart(bool enabled, bool alreadyActive) => enabled && !alreadyActive;

    public static double TargetVolume(double current, double cockpit, double level) =>
        Math.Min(current, cockpit * Math.Clamp(double.IsFinite(level) ? level : 0.4, 0, 1));

    public static bool ShouldRestore(double current, double applied) =>
        double.IsFinite(current) && double.IsFinite(applied) && Math.Abs(current - applied) <= 1.0 / 65536;

    public static bool ReleaseDue(DateTimeOffset now, DateTimeOffset? stoppedAt) =>
        stoppedAt is { } stopped && now - stopped >= ReleaseDelay;
}
