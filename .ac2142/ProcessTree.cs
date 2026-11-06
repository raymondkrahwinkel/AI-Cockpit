using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class ProcessTree
{
    internal sealed record Snapshot(int[] Pids, double CpuMilliseconds, long WorkingSetBytes, long PrivateBytes);

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
        foreach (int pid in owned)
        {
            using Process process = Process.GetProcessById(pid);
            cpu += process.TotalProcessorTime.TotalMilliseconds;
            workingSet += process.WorkingSet64;
            privateBytes += process.PrivateMemorySize64;
        }
        return new Snapshot(owned.Order().ToArray(), cpu, workingSet, privateBytes);
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
