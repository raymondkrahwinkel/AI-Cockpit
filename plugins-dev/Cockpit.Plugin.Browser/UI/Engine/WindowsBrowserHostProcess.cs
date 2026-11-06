using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

[SupportedOSPlatform("windows")]
internal static class WindowsBrowserHostProcess
{
    internal static Process Start(string executable, IReadOnlyList<string> arguments, BrowserIpcPipes pipes)
    {
        nint size = 0;
        _InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint attributes = Marshal.AllocHGlobal(size);
        nint handles = Marshal.AllocHGlobal(2 * nint.Size);
        bool initialized = false;
        try
        {
            if (_InitializeProcThreadAttributeList(attributes, 1, 0, ref size) == false)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            initialized = true;
            Marshal.WriteIntPtr(handles, pipes.Output.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, pipes.Input.ClientSafePipeHandle.DangerousGetHandle());
            if (_UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, 2 * nint.Size, 0, 0) == false)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var command = new StringBuilder(_Quote(executable));
            foreach (string argument in arguments)
            {
                command.Append(' ').Append(_Quote(argument));
            }
            // The explicit handle list prevents unrelated inheritable capabilities reaching this child (AC-2144).
            if (_CreateProcess(executable, command, 0, 0, true, 0x00080000 | 0x08000000, 0,
                Path.GetDirectoryName(executable), ref startup, out var information) == false)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            try
            {
                var process = Process.GetProcessById(information.ProcessId);
                _ = process.SafeHandle;
                return process;
            }
            catch
            {
                _TerminateProcess(information.Process, 1);
                throw;
            }
            finally
            {
                _CloseHandle(information.Thread);
                _CloseHandle(information.Process);
            }
        }
        finally
        {
            if (initialized)
            {
                _DeleteProcThreadAttributeList(attributes);
            }
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(pipes);
        }
    }

    private static string _Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal short Show, ReservedSize;
        internal nint ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo Startup;
        internal nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    private static extern bool _InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    private static extern bool _UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returned);

    [DllImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
    private static extern void _DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool _CreateProcess(string executable, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    private static extern bool _CloseHandle(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess")]
    private static extern bool _TerminateProcess(nint process, uint exitCode);
}
