using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Sessions;

// AC-1060: reads the cgroup a session actually runs in, through `/proc/<pid>/cgroup` rather than through the
// path `LinuxCgroupMemoryLimiter` wrote — so it stays right for a process that was moved after it was capped.
public static class LinuxSessionCgroup
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    // The group's own name, which is what `systemd-oomd` logs when it kills one. Null when there is no such
    // group, so a platform without cgroups and a test with an invented pid both get the same quiet answer.
    public static string? NameFor(int processId) =>
        PathFor(processId) is { } path ? Path.GetFileName(path) : null;

    // The `some avg10` of this session's group. Null when the group is gone or unreadable — a session that
    // ended between the sample and this read is ordinary here, not an error.
    public static double? PressureAvg10(int processId)
    {
        if (PathFor(processId) is not { } path)
        {
            return null;
        }

        try
        {
            return CgroupPressureLine.SomeAvg10(File.ReadAllText(Path.Combine(path, "memory.pressure")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The directory must really be there: `/proc/<pid>/cgroup` still names a group for a process on its way
    // out, and a name with nothing behind it is not something to measure or to match a kill against.
    public static string? PathFor(int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines($"/proc/{processId}/cgroup"))
            {
                if (!line.StartsWith("0::", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = CgroupRoot + line[3..].TrimEnd();
                return Directory.Exists(path) ? path : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No /proc entry means the process has exited, which every caller here treats as "cannot say".
        }

        return null;
    }
}
