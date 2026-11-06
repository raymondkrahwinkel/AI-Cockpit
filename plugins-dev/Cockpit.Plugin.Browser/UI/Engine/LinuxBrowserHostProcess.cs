using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

[SupportedOSPlatform("linux")]
internal static class LinuxBrowserHostProcess
{
    internal static Process Start(string executable, IReadOnlyList<string> arguments, BrowserIpcPipes pipes)
    {
        int input = -1;
        int output = -1;
        var actions = new FileActions();
        _Check(_ActionsInit(ref actions));
        var allocated = new List<nint>();
        try
        {
            // Duplicate above the destination fds before scheduling dup2, so no source is overwritten.
            input = _Fcntl(pipes.Output.ClientSafePipeHandle.DangerousGetHandle().ToInt32(), 1030, 5);
            output = _Fcntl(pipes.Input.ClientSafePipeHandle.DangerousGetHandle().ToInt32(), 1030, 5);
            if (input < 0 || output < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            _Check(_AddDup2(ref actions, input, 3));
            _Check(_AddDup2(ref actions, output, 4));
            _Check(_AddClose(ref actions, 0));
            _Check(_AddClose(ref actions, 1));
            _Check(_AddClose(ref actions, 2));
            // glibc closefrom is performed in the child only, excluding every unrelated inherited descriptor.
            _Check(_AddCloseFrom(ref actions, 5));
            string[] childArguments = arguments.Select(argument =>
                argument.StartsWith("--cockpit-input=", StringComparison.Ordinal) ? "--cockpit-input=3" :
                argument.StartsWith("--cockpit-output=", StringComparison.Ordinal) ? "--cockpit-output=4" : argument).ToArray();
            nint argv = _Strings([executable, .. childArguments], allocated);
            nint environment = _Strings(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(entry => $"{entry.Key}={entry.Value}").ToArray(), allocated);
            _Check(_Spawn(out int pid, executable, ref actions, 0, argv, environment));
            try
            {
                return Process.GetProcessById(pid);
            }
            catch
            {
                _Kill(pid, 9);
                _WaitPid(pid, out _, 0);
                throw;
            }
        }
        finally
        {
            _ActionsDestroy(ref actions);
            if (input >= 0)
            {
                _Close(input);
            }
            if (output >= 0)
            {
                _Close(output);
            }
            foreach (nint pointer in allocated)
            {
                Marshal.FreeHGlobal(pointer);
            }
            GC.KeepAlive(pipes);
        }
    }

    internal static void Reap(int pid)
    {
        int result;
        do
        {
            result = _WaitPid(pid, out _, 0);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0 && Marshal.GetLastPInvokeError() != 10)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static nint _Strings(IReadOnlyList<string> values, List<nint> allocated)
    {
        nint array = Marshal.AllocHGlobal((values.Count + 1) * nint.Size);
        allocated.Add(array);
        for (int i = 0; i < values.Count; i++)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(values[i] + '\0');
            nint copy = Marshal.AllocHGlobal(bytes.Length);
            allocated.Add(copy);
            Marshal.Copy(bytes, 0, copy, bytes.Length);
            Marshal.WriteIntPtr(array, i * nint.Size, copy);
        }
        Marshal.WriteIntPtr(array, values.Count * nint.Size, 0);
        return array;
    }

    private static void _Check(int error)
    {
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    // glibc reserves sixteen ints after its counters and pointer on both supported 64-bit architectures.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    private struct FileActions { }

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int _ActionsInit(ref FileActions actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int _ActionsDestroy(ref FileActions actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int _AddDup2(ref FileActions actions, int source, int destination);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    private static extern int _AddClose(ref FileActions actions, int descriptor);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")]
    private static extern int _AddCloseFrom(ref FileActions actions, int first);
    [DllImport("libc", EntryPoint = "posix_spawn", CharSet = CharSet.Ansi)]
    private static extern int _Spawn(out int pid, string path, ref FileActions actions, nint attributes, nint argv, nint environment);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int _Fcntl(int descriptor, int command, int value);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int _Close(int descriptor);
    [DllImport("libc", EntryPoint = "kill")]
    private static extern int _Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int _WaitPid(int pid, out int status, int options);
}
