using System.Runtime.Versioning;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>Linux's process table, read straight from <c>/proc</c> (#78) — no shelling out, so it is cheap enough to do every couple of seconds.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ProcProcessTableReader : IProcessTableReader, ISingletonService
{
    // Kernel ticks per second: 100 on any Linux worth running. sysconf(_SC_CLK_TCK) needs a P/Invoke, and being
    // wrong here would scale the CPU percentage, not break it.
    private const double TicksPerSecond = 100;

    public IReadOnlyList<ProcessRow> Read()
    {
        var rows = new List<ProcessRow>();

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId))
            {
                continue;
            }

            if (_ReadStat(processId) is not { } stat)
            {
                continue;
            }

            rows.Add(new ProcessRow(
                processId,
                stat.ParentProcessId,
                TimeSpan.FromSeconds(stat.TotalTicks / TicksPerSecond),
                _ReadResidentMemory(processId)));
        }

        return rows;
    }

    private static ProcStatLine? _ReadStat(int processId)
    {
        try
        {
            return ProcStatLine.Parse(File.ReadAllText($"/proc/{processId}/stat"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A process that exited between listing the directory and reading it is the normal case here.
            return null;
        }
    }

    // VmRSS: what the process actually occupies, which is what an operator means by "how much RAM is this using".
    private static long _ReadResidentMemory(int processId)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{processId}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes) ? kilobytes * 1024 : 0;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        return 0;
    }
}
