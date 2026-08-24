using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cockpit.Core.Diagnostics;

// AC-1013 (AC-57/AC-58): Cockpit's own memory, split into the figure that matters (`ResidentBytes`) and the one
// that misleads (`VirtualBytes`, inflated to tens of GB by .NET's region GC on 64-bit Linux/Windows) — AC-57
// began as a "62 GB" panic that was this reservation. `PrivateBytes`/`PeakResidentBytes`/`SwapBytes` (Linux only) round it out.
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

    // AC-1013 (AC-57): .NET returns 0 for Process.PeakWorkingSet64 on macOS, hiding whether resident ever spiked
    // (Rick's trace showed 272 MB resident but "Peak resident: 0 B"). Read natively via getrusage's ru_maxrss
    // (bytes on Darwin, unlike Linux's kilobytes); any failure falls back to the framework value.
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
