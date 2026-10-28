using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cockpit.Plugin.Browser;

// Keep handles to our descendants before asking Chromium to exit: WaitForExit on the root alone does not wait for them.
internal sealed class BrowserProcessTree(Process root) : IDisposable
{
    private readonly List<Process> _children = [];

    public int Capture()
    {
        var before = _children.Count;
        var candidates = new List<(Process Process, int Parent)>();
        foreach (var process in Process.GetProcesses())
        {
            try { candidates.Add((process, ParentId(process))); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            { process.Dispose(); }
        }

        var parents = new Dictionary<int, Process> { [root.Id] = root };
        foreach (var child in _children)
        {
            parents[child.Id] = child;
        }
        var added = true;
        while (added)
        {
            added = false;
            foreach (var (process, parentId) in candidates)
            {
                try
                {
                    if (!parents.ContainsKey(process.Id) && parents.TryGetValue(parentId, out var parent)
                        && process.StartTime >= parent.StartTime
                        && (!parent.HasExited || process.StartTime <= parent.ExitTime))
                    {
                        parents[process.Id] = process;
                        _children.Add(process);
                        added = true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        foreach (var (process, _) in candidates)
        {
            if (!_children.Contains(process))
            {
                process.Dispose();
            }
        }
        return _children.Count - before;
    }

    public bool Wait(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        return new[] { root }.Concat(_children).All(process => Wait(process, timeout - elapsed.Elapsed));
    }

    public void Kill()
    {
        // Capture any descendants created while the graceful close was in flight, before the root disappears.
        Capture();
        foreach (var process in new[] { root }.Concat(_children))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
        }
    }

    private static bool Wait(Process process, TimeSpan remaining)
    {
        try { return process.HasExited || process.WaitForExit((int)Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue)); }
        catch (InvalidOperationException) { return true; }
    }

    public void Dispose()
    {
        foreach (var child in _children)
        {
            child.Dispose();
        }
    }

    private static int ParentId(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            // Query the opened process handle, rather than trusting a potentially recycled PID from a table.
            return NtQueryInformationProcess(process.SafeHandle.DangerousGetHandle(), 0, out var info, Marshal.SizeOf<BasicInformation>(), out _) == 0
                ? checked((int)info.ParentId) : -1;
        }
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        using var ps = Process.Start(new ProcessStartInfo("ps", $"-o ppid= -p {process.Id}")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        return ps is not null && int.TryParse(ps.StandardOutput.ReadToEnd().Trim(), out var parent) ? parent : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint Reserved1;
        public nint Peb;
        public nint Reserved2;
        public nint Reserved3;
        public nint ProcessId;
        public nint ParentId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, out BasicInformation information, int size, out int returned);
}
