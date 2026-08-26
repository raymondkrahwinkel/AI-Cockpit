using System.Globalization;

namespace Cockpit.Core.Diagnostics;

// AC-1060: the `memory.pressure` file of a cgroup v2 group. Only the `some` line is read — `full` counts just
// the time where every task in the group stalled, which a session with an idle thread never reaches.
public static class CgroupPressureLine
{
    // The ten-second average, the same window `systemd-oomd` decides on. Null when the file is not the shape
    // this expects, which is what a caller reading an ordinary directory gets.
    public static double? SomeAvg10(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("some ", StringComparison.Ordinal))
            {
                continue;
            }

            return _Field(line, "avg10=");
        }

        return null;
    }

    private static double? _Field(string line, string name)
    {
        var at = line.IndexOf(name, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var value = line[(at + name.Length)..];
        var end = value.IndexOf(' ');
        if (end >= 0)
        {
            value = value[..end];
        }

        // The kernel writes these with a '.' whatever the machine's locale is, so the invariant culture is the
        // only correct reading of them.
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
