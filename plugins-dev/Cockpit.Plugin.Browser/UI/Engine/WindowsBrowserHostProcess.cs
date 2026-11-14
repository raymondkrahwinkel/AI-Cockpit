using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Cockpit.Plugin.Browser.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.UI.Engine;

[SupportedOSPlatform("windows")]
internal static class WindowsBrowserHostProcess
{
    internal static (Process Process, SafeFileHandle Job, SafeFileHandle CompletionPort) Start(string executable, IReadOnlyList<string> arguments, BrowserIpcPipes pipes)
    {
        SafeFileHandle job = _CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }
        nint size = 0;
        nint attributes = 0;
        nint handles = 0;
        bool initialized = false;
        SafeFileHandle? completionPort = null;
        try
        {
            completionPort = _CreateIoCompletionPort(-1, 0, 0, 1);
            if (completionPort.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            var association = new CompletionPortInformation { Port = completionPort.DangerousGetHandle() };
            if (!_AssociateCompletionPort(job, 7, ref association, Marshal.SizeOf<CompletionPortInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x00002000 } };
            if (_SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()) == false)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            _InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            if (_InitializeProcThreadAttributeList(attributes, 2, 0, ref size) == false)
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
            Marshal.WriteIntPtr(handles, 2 * nint.Size, job.DangerousGetHandle());
            // AC-2145: assign during creation so an owner crash cannot strand even the suspended host.
            if (_UpdateProcThreadAttribute(attributes, 0, 0x2000D, handles + 2 * nint.Size, nint.Size, 0, 0) == false)
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
            if (_CreateProcess(executable, command, 0, 0, true, 0x00080000 | 0x08000000 | 0x00000004, 0,
                Path.GetDirectoryName(executable), ref startup, out var information) == false)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            Process? process = null;
            try
            {
                process = Process.GetProcessById(information.ProcessId);
                _ = process.SafeHandle;
                if (_ResumeThread(information.Thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                SafeFileHandle port = completionPort;
                completionPort = null;
                return (process, job, port);
            }
            catch
            {
                _TerminateProcess(information.Process, 1);
                process?.Dispose();
                throw;
            }
            finally
            {
                _CloseHandle(information.Thread);
                _CloseHandle(information.Process);
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            if (initialized)
            {
                _DeleteProcThreadAttributeList(attributes);
            }
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
            completionPort?.Dispose();
            GC.KeepAlive(pipes);
            GC.KeepAlive(job);
        }
    }

    private static void _Terminate(SafeFileHandle job)
    {
        if (!_TerminateJobObject(job, 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    internal static Task StopAsync(SafeFileHandle job, SafeFileHandle port) => Task.Run(() =>
    {
        using (job)
        using (port)
        {
            _Terminate(job);
            if (_IsEmpty(job))
            {
                return;
            }
            long deadline = Environment.TickCount64 + 5_000;
            while (true)
            {
                uint remaining = (uint)Math.Max(0, deadline - Environment.TickCount64);
                if (!_GetQueuedCompletionStatus(port, out uint message, out _, out _, remaining))
                {
                    int error = Marshal.GetLastPInvokeError();
                    // Job notifications can be lost; accounting confirms exit without polling.
                    if (error == 258 && _IsEmpty(job))
                    {
                        return;
                    }
                    throw new Win32Exception(error);
                }
                if (message == 7) // JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO includes every CEF descendant.
                {
                    return;
                }
                if (remaining == 0)
                {
                    throw new TimeoutException("The browser process tree did not confirm its shutdown.");
                }
            }
        }
    });

    private static bool _IsEmpty(SafeFileHandle job)
    {
        if (!_QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<AccountingInformation>(), out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return accounting.ActiveProcesses == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInformation
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, Processes, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    private static extern bool _TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    private static extern bool _QueryInformationJobObject(SafeFileHandle job, int informationClass, out AccountingInformation information, int size, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionPortInformation
    {
        internal nint Key, Port;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateIoCompletionPort", SetLastError = true)]
    private static extern SafeFileHandle _CreateIoCompletionPort(nint file, nint existing, nuint key, uint threads);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    private static extern bool _AssociateCompletionPort(SafeFileHandle job, int informationClass, ref CompletionPortInformation information, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetQueuedCompletionStatus", SetLastError = true)]
    private static extern bool _GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out nuint key, out nint overlapped, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
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

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle _CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    private static extern bool _SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, int size);

    [DllImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    private static extern uint _ResumeThread(nint thread);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess")]
    private static extern bool _TerminateProcess(nint process, uint exitCode);
}
