using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Plugin.Browser.Host;

namespace Cockpit.Plugin.Browser.Subprocess;

[SupportedOSPlatform("linux")]
internal static class LinuxBrowserSupervisor
{
    // shortcut: a killed supervisor cannot reap a hung host; use a kernel lifetime boundary if that must be covered.
    internal static int Run(string[] args)
    {
        if (_Prctl(36, 1, 0, 0, 0) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var lifetime = new PollDescriptor { Descriptor = 5, Events = 1 };
        if (_ParentGone(ref lifetime, 0))
        {
            return 1;
        }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        int host = LinuxBrowserProcess.Spawn(executable, args.Where(argument => argument != "--cockpit-supervisor").ToArray(), [3, 4], newGroup: true);
        try
        {
            LinuxBrowserProcess.Close(3);
            LinuxBrowserProcess.Close(4);
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, host);
            if (_Write(6, bytes, (nuint)bytes.Length) != bytes.Length)
            {
                throw new IOException("The browser supervisor could not report its host.");
            }
            LinuxBrowserProcess.Close(6);
            while (LinuxBrowserProcess.HasExited(host) == false)
            {
                if (_ParentGone(ref lifetime, 100))
                {
                    break;
                }
            }
            return 0;
        }
        finally
        {
            // Stay outside the host group and adopt its orphans so cleanup also removes zombies.
            LinuxBrowserProcess.KillGroup(host);
            while (LinuxBrowserProcess.Wait(-1, out _) > 0)
            {
            }
        }
    }

    private static bool _ParentGone(ref PollDescriptor descriptor, int timeout)
    {
        int result = _Poll(ref descriptor, 1, timeout);
        if (result < 0 && Marshal.GetLastPInvokeError() != 4)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return result > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int Descriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int _Prctl(int option, nuint value, nuint third, nuint fourth, nuint fifth);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int _Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint _Write(int descriptor, byte[] bytes, nuint count);
}
