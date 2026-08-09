using System.Buffers;
using System.Runtime.Versioning;
using System.Text;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

// Linux's process table, read straight from `/proc` (#78) — no shelling out, so it is cheap enough to do every couple of seconds.
[SupportedOSPlatform("linux")]
internal sealed class ProcProcessTableReader : IProcessTableReader
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
                _ReadResidentMemory(processId),
                stat.Name));
        }

        return rows;
    }

    private static ProcStatLine? _ReadStat(int processId)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);

        try
        {
            var read = _ReadInto($"/proc/{processId}/stat", buffer);

            return read == 0 ? null : ProcStatLine.Parse(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A process that exited between listing the directory and reading it is the normal case here.
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // One pooled read instead of File.ReadAllText, because this runs for every process on the machine every couple of
    // seconds and a `/proc` file is the case ReadAllText is worst at: the kernel reports its length as zero, so there
    // is nothing to size a buffer from and the read grows one under a FileStream and a StreamReader, per process, per
    // tick. Measured on this machine that machinery cost 18.7 KB per process — 656 processes, 12 MB a tick — for
    // files of a few hundred bytes.
    //
    // A read that fills the buffer is reported as nothing rather than as content. These files come from seq_file
    // generators that answer a single read in full, so it does not happen at this size — but "did not fit" and "is
    // all of it" are indistinguishable afterwards, and the failure that would follow is the quiet kind: a `statm`
    // cut mid-field still has its two spaces and still parses, into a resident figure that is simply too small and
    // goes straight to the status bar with nothing to say it is wrong.
    private static int _ReadInto(string path, byte[] buffer)
    {
        using var handle = File.OpenHandle(path);

        var read = RandomAccess.Read(handle, buffer, 0);

        return read == buffer.Length ? 0 : read;
    }

    // Comfortably over a `stat` line for a process with a long name, and over any `statm`.
    private const int BufferBytes = 4096;

    // What the process actually occupies, which is what an operator means by "how much RAM is this using".
    //
    // From `statm` rather than `status`, because this runs for every process on the machine every couple of seconds
    // and `status` is the expensive way to ask: ~1.5 KB over some fifty lines, scanned line by line — a string per
    // line and a reader per process — to reach one field. `statm` is a single short line of counts. Measured on this
    // machine, its second field times the page size equals `VmRSS` exactly, so the number on screen does not move.
    //
    // Not `stat`'s own rss field, which would have saved the second read altogether: measured against `VmRSS` it sat
    // about 1.5% low (733.9 vs 744.8 MB on a running cockpit), which is what the kernel's own documentation warns
    // about. Cheaper, and wrong.
    private static long _ReadResidentMemory(int processId)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);

        try
        {
            var read = _ReadInto($"/proc/{processId}/statm", buffer);

            if (read == 0)
            {
                return 0;
            }

            // Fields are space-separated counts of pages: total, resident, shared, … — the second is the one we want.
            // Read straight off the buffer: the digits are ASCII, so there is no string to make here at all.
            var statm = buffer.AsSpan(0, read);
            var afterTotal = statm[(statm.IndexOf((byte)' ') + 1)..];
            var resident = afterTotal[..afterTotal.IndexOf((byte)' ')];

            return long.TryParse(resident, out var pages) ? pages * Environment.SystemPageSize : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Same normal case as the stat read above: a process that exited between listing and reading. The
            // out-of-range arm covers a truncated read of a process on its way out, where the second space is gone.
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
