using System.Management;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// Windows's process table, via WMI's `Win32_Process` (#78): Windows has no `/proc` and .NET exposes no
// parent-process id. `wmic` would have been simpler but is being removed from Windows. Times come back
// as 100-nanosecond units of kernel and user mode; memory as bytes already.
[SupportedOSPlatform("windows")]
internal sealed class WmiProcessTableReader : IProcessTableReader
{
    public IReadOnlyList<ProcessRow> Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, KernelModeTime, UserModeTime, WorkingSetSize, Name FROM Win32_Process");

            var rows = new List<ProcessRow>();
            foreach (var item in searcher.Get())
            {
                using var process = (ManagementObject)item;

                var processId = _ToInt(process["ProcessId"]);
                if (processId <= 0)
                {
                    continue;
                }

                var kernel = _ToLong(process["KernelModeTime"]);
                var user = _ToLong(process["UserModeTime"]);

                rows.Add(new ProcessRow(
                    processId,
                    _ToInt(process["ParentProcessId"]),
                    TimeSpan.FromTicks(kernel + user),
                    _ToLong(process["WorkingSetSize"]),
                    process["Name"] as string ?? string.Empty));
            }

            return rows;
        }
        catch (ManagementException)
        {
            // WMI unavailable or refused: show nothing rather than a wrong number.
            return [];
        }
    }

    private static int _ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    private static long _ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value);
}
