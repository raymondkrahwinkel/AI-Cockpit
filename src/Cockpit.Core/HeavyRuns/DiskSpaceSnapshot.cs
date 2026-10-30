namespace Cockpit.Core.HeavyRuns;

public enum DiskPressure
{
    Normal,
    Elevated,
    Critical,
}

public sealed record DiskSpaceSnapshot(string Name, long TotalBytes, long FreeBytes, TimeSpan? TimeToFull, DiskPressure Pressure);
