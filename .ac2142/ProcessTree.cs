using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class ProcessTree
{
    internal sealed record Snapshot(int[] Pids, int[] ExitedPids, int[] UnobservedPids, double CpuMilliseconds, long WorkingSetBytes, long PrivateBytes);
    private static readonly List<Process> Observed = [];

    internal static Snapshot Capture()
    {
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var parents = new Dictionary<int, int>();
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = string.Empty };
            bool more = Process32First(snapshot, ref entry);
            while (more)
            {
                parents[(int)entry.Pid] = (int)entry.Parent;
                more = Process32Next(snapshot, ref entry);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        var owned = new HashSet<int> { Environment.ProcessId };
        int previous;
        do
        {
            previous = owned.Count;
            foreach ((int pid, int parent) in parents)
            {
                if (owned.Contains(parent))
                {
                    owned.Add(pid);
                }
            }
        } while (previous != owned.Count);
        double cpu = 0;
        long workingSet = 0;
        long privateBytes = 0;
        var unobserved = new List<int>();
        foreach (int pid in owned)
        {
            if (Observed.Any(process => process.Id == pid && process.HasExited == false))
            {
                continue;
            }
            try
            {
                Process process = Process.GetProcessById(pid);
                // Retain the handle so a child's final CPU time remains readable after it exits.
                _ = process.Handle;
                Observed.Add(process);
            }
            catch (ArgumentException)
            {
                unobserved.Add(pid);
            }
        }
        var exited = new List<int>();
        foreach (Process process in Observed)
        {
            process.Refresh();
            long rss = 0;
            long privateMemory = 0;
            try
            {
                if (process.HasExited == false)
                {
                    rss = process.WorkingSet64;
                    privateMemory = process.PrivateMemorySize64;
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                rss = 0;
                privateMemory = 0;
            }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited)
            {
                rss = 0;
                privateMemory = 0;
            }
            if (process.HasExited)
            {
                exited.Add(process.Id);
            }
            cpu += process.TotalProcessorTime.TotalMilliseconds;
            workingSet += rss;
            privateBytes += privateMemory;
        }
        return new Snapshot(Observed.Select(process => process.Id).Order().ToArray(), exited.ToArray(), unobserved.ToArray(), cpu, workingSet, privateBytes);
    }

    internal static void Dispose()
    {
        foreach (Process process in Observed)
        {
            process.Dispose();
        }
        Observed.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size;
        public uint Usage;
        public uint Pid;
        public nuint Heap;
        public uint Module;
        public uint Threads;
        public uint Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref Entry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref Entry entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
