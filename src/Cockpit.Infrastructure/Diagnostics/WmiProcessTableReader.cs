using System.Management;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>
/// Windows's process table, via WMI's <c>Win32_Process</c> (#78). Windows has no <c>/proc</c> and .NET exposes no
/// parent-process id, so this is the supported way to learn who spawned what. <c>wmic</c> would have been simpler
/// and is being removed from Windows, which is not a foundation to build on.
/// <para>
/// The times come back as 100-nanosecond units of kernel and user mode; the memory as bytes already.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WmiProcessTableReader : IProcessTableReader, ISingletonService
{
    public IReadOnlyList<ProcessRow> Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, KernelModeTime, UserModeTime, WorkingSetSize FROM Win32_Process");

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
                    _ToLong(process["WorkingSetSize"])));
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
