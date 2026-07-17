using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cockpit.Core.Diagnostics;

/// <summary>
/// The cockpit process's own memory, split into the figure that matters and the figure that misleads (AC-57/AC-58).
/// <see cref="ResidentBytes"/> is what the process actually occupies; <see cref="VirtualBytes"/> is the address
/// space it reserved, which the .NET region GC inflates to tens of gigabytes on 64-bit Linux/Windows without using
/// any of it. AC-57 started as a "62 GB" panic that was this reservation — so the panel shows resident first and
/// labels virtual for what it is.
/// </summary>
/// <param name="ResidentBytes">Working set — physical memory in use now.</param>
/// <param name="PeakResidentBytes">The highest the working set has reached this run.</param>
/// <param name="VirtualBytes">Reserved address space. Large is normal; it is not memory in use.</param>
/// <param name="PrivateBytes">Committed private memory — closer to the real cost than virtual, minus shared mappings.</param>
/// <param name="SwapBytes">Swapped-out pages, when the platform reports them (Linux); null elsewhere.</param>
public sealed record ProcessMemoryInfo(
    long ResidentBytes,
    long PeakResidentBytes,
    long VirtualBytes,
    long PrivateBytes,
    long? SwapBytes)
{
    public static ProcessMemoryInfo Current()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new ProcessMemoryInfo(
            process.WorkingSet64,
            process.PeakWorkingSet64,
            process.VirtualMemorySize64,
            process.PrivateMemorySize64,
            _SwapBytes());
    }

    // Only Linux exposes a process's own swapped-out size cheaply, via VmSwap in /proc/self/status (kB). Windows
    // and macOS have no equivalent per-process figure without a native call, so the panel omits it there rather
    // than reporting a guessed zero.
    private static long? _SwapBytes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("VmSwap:", StringComparison.Ordinal))
                {
                    continue;
                }

                // The line is "VmSwap:\t       0 kB": take the number before the unit.
                var value = line.AsSpan("VmSwap:".Length).Trim();
                var unitStart = value.IndexOf(' ');
                if (unitStart > 0)
                {
                    value = value[..unitStart];
                }

                return long.TryParse(value, out var kilobytes) ? kilobytes * 1024 : null;
            }
        }
        catch (IOException)
        {
            // A kernel without /proc, or a sandbox that hides it: swap is simply unknown, not zero.
        }

        return null;
    }
}
