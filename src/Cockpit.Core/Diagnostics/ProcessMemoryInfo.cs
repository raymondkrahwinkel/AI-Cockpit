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
            _PeakResidentBytes(process),
            process.VirtualMemorySize64,
            process.PrivateMemorySize64,
            _SwapBytes());
    }

    // .NET does not populate Process.PeakWorkingSet64 on macOS — it returns 0 there. In the diagnostics panel that
    // read as a false "0 B" and hid the one figure AC-57 needs: whether resident ever spiked this run, even after
    // it settled back (Rick's trace showed 272 MB resident but "Peak resident: 0 B", so a mid-run explosion would
    // leave no trace). macOS exposes the peak cheaply through getrusage's ru_maxrss — in bytes on Darwin, unlike
    // Linux where it is kilobytes — so read it natively there. Any failure falls back to the framework value: a
    // missing or wrong peak must never take the panel down.
    private static long _PeakResidentBytes(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                if (getrusage(_RusageSelf, out var usage) == 0 && usage.MaxResidentSetBytes > 0)
                {
                    return usage.MaxResidentSetBytes;
                }
            }
            catch (DllNotFoundException)
            {
                // No libc to call (should not happen on macOS): fall through to the framework value.
            }
            catch (EntryPointNotFoundException)
            {
                // getrusage absent: same fallback.
            }
        }

        return process.PeakWorkingSet64;
    }

    private const int _RusageSelf = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int getrusage(int who, out RUsage usage);

    // macOS layout of struct rusage. Only ru_maxrss is read; the two leading timevals — 16 bytes each on 64-bit
    // Darwin (an 8-byte tv_sec and a 4-byte tv_usec, padded to 8) — put it at offset 32. Size spans the whole
    // struct (2 timevals + 14 longs = 144) so the marshaller copies a valid amount for the kernel to fill.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct RUsage
    {
        [FieldOffset(32)]
        public long MaxResidentSetBytes;
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
