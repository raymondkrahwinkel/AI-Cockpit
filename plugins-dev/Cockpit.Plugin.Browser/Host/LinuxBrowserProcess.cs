using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cockpit.Plugin.Browser.Host;

[SupportedOSPlatform("linux")]
internal static class LinuxBrowserProcess
{
    internal static int Spawn(string executable, IReadOnlyList<string> arguments, int[] descriptors, bool newGroup = false, int standardError = -1)
    {
        var actions = new FileActions();
        var attributes = new SpawnAttributes();
        _Check(_ActionsInit(ref actions));
        var copies = new List<int>();
        var allocated = new List<nint>();
        bool attributesInitialized = false;
        int errorCopy = -1;
        try
        {
            _Check(_AttributesInit(ref attributes));
            attributesInitialized = true;
            if (newGroup)
            {
                _Check(_SetGroup(ref attributes, 0));
                _Check(_SetFlags(ref attributes, 2));
            }
            foreach (int descriptor in descriptors)
            {
                int copy = _Fcntl(descriptor, 1030, 3 + descriptors.Length);
                if (copy < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                copies.Add(copy);
                _Check(_AddDup2(ref actions, copy, 2 + copies.Count));
            }
            if (standardError >= 0)
            {
                errorCopy = _Fcntl(standardError, 1030, 3 + descriptors.Length);
                if (errorCopy < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                _Check(_AddDup2(ref actions, errorCopy, 2));
            }
            for (int descriptor = 0; descriptor < (standardError >= 0 ? 2 : 3); descriptor++)
            {
                _Check(_AddOpen(ref actions, descriptor, "/dev/null", 2, 0));
            }
            try
            {
                _Check(_AddCloseFrom(ref actions, 3 + descriptors.Length));
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PlatformNotSupportedException("The browser host needs glibc 2.34 or newer to restrict pipe inheritance.", exception);
            }
            nint argv = _Strings([executable, .. arguments], allocated);
            nint environment = _Strings(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(entry => $"{entry.Key}={entry.Value}").ToArray(), allocated);
            _Check(_Spawn(out int pid, executable, ref actions, ref attributes, argv, environment));
            return pid;
        }
        finally
        {
            _ActionsDestroy(ref actions);
            if (attributesInitialized)
            {
                _AttributesDestroy(ref attributes);
            }
            foreach (int copy in copies)
            {
                Close(copy);
            }
            if (errorCopy >= 0)
            {
                Close(errorCopy);
            }
            foreach (nint pointer in allocated)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    internal static int Wait(int pid, out int status, int options = 0)
    {
        int result;
        do
        {
            result = _WaitPid(pid, out status, options);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0 && Marshal.GetLastPInvokeError() != 10)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return result;
    }

    internal static void KillGroup(int pid)
    {
        if (_Kill(-pid, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal static bool HasExited(int pid)
    {
        int result;
        SignalInfo info;
        do
        {
            result = _WaitId(1, pid, out info, 4 | 1 | 0x01000000);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return info.ProcessId != 0;
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

    // glibc's opaque spawn structures have these sizes on the supported Linux x64/arm64 runtimes.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    private struct FileActions { }
    [StructLayout(LayoutKind.Sequential, Size = 336)]
    private struct SpawnAttributes { }

    // WNOWAIT keeps the leader's PID reserved until the group has been killed.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct SignalInfo
    {
        [FieldOffset(16)] internal int ProcessId;
    }

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int _ActionsInit(ref FileActions actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int _ActionsDestroy(ref FileActions actions);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static extern int _AttributesInit(ref SpawnAttributes attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static extern int _AttributesDestroy(ref SpawnAttributes attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    private static extern int _SetGroup(ref SpawnAttributes attributes, int group);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static extern int _SetFlags(ref SpawnAttributes attributes, short flags);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int _AddDup2(ref FileActions actions, int source, int destination);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    private static extern int _AddOpen(ref FileActions actions, int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")]
    private static extern int _AddCloseFrom(ref FileActions actions, int first);
    [DllImport("libc", EntryPoint = "posix_spawn")]
    private static extern int _Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref FileActions actions, ref SpawnAttributes attributes, nint argv, nint environment);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int _Fcntl(int descriptor, int command, int value);
    [DllImport("libc", EntryPoint = "close")]
    internal static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int _Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int _WaitPid(int pid, out int status, int options);
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)]
    private static extern int _WaitId(int type, int pid, out SignalInfo info, int options);
}
