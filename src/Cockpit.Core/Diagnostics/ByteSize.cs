using System.Globalization;

namespace Cockpit.Core.Diagnostics;

/// <summary>
/// Formats a byte count for a human reading the diagnostics panel (AC-58): the unit that keeps the number in the
/// 1–1024 range, so "680 MB" and "73.6 GB" rather than a wall of digits. Binary units (1024), matching what
/// Activity Monitor and the status bar already show.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Human(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal below 100 so 73.6 GB keeps its detail; none above, where the fraction is noise.
        var format = value >= 100 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
