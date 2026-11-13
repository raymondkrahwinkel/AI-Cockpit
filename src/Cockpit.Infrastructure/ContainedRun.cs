using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Infrastructure;

// AC-2333 S5: a command started by CommandRunnerProcess.StartContained. Disposing it ends every process it started.
internal sealed class ContainedRun(
    StreamReader output, StreamReader error, Func<CancellationToken, Task> waitForExit, Func<int?> exitCode, Action end) : IDisposable
{
    private Action? _end = end;

    public StreamReader Output => output;

    public StreamReader Error => error;

    // Null while the command still runs.
    public int? ExitCode => exitCode();

    public Task WaitForExitAsync(CancellationToken cancellationToken) => waitForExit(cancellationToken);

    public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
}

// The command is created suspended, put in a kill-on-close job and only then resumed, so not even its first child is
// born outside the job. A job that cannot be set up ends the process before it ran a single instruction.
[SupportedOSPlatform("windows")]
internal static class WindowsContainedStart
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StillActive = 259;

    public static ContainedRun Start(string executable, IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string> environment)
    {
        var job = _KillOnCloseJob();
        var (outputRead, outputWrite) = _Pipe();
        var (errorRead, errorWrite) = _Pipe();
        var (inputRead, inputWrite) = _Pipe(childReads: true);
        var startupInfo = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StdInput = inputRead.DangerousGetHandle(),
            StdOutput = outputWrite.DangerousGetHandle(),
            StdError = errorWrite.DangerousGetHandle(),
        };
        var block = GCHandle.Alloc(_EnvironmentBlock(environment), GCHandleType.Pinned);
        ProcessInformation info;
        bool created;
        try
        {
            created = NativeMethods.CreateProcessW(
                null, ConPtyHostFactory.BuildCommandLine(executable, arguments), IntPtr.Zero, IntPtr.Zero, bInheritHandles: true,
                CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, block.AddrOfPinnedObject(), directory, ref startupInfo, out info);
        }
        finally
        {
            block.Free();
            outputWrite.Dispose();
            errorWrite.Dispose();
            inputRead.Dispose();

            // The command gets an empty stdin, as it did through Process.
            inputWrite.Dispose();
        }

        if (!created)
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
            outputRead.Dispose();
            errorRead.Dispose();
            throw failure;
        }

        if (!WindowsJobSessionAnchor.NativeMethods.AssignProcessToJobObject(job, info.Process) || NativeMethods.ResumeThread(info.Thread) == uint.MaxValue)
        {
            var failure = Marshal.GetLastWin32Error();
            NativeMethods.TerminateProcess(info.Process, 1);
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(info.Thread);
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(info.Process);
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
            outputRead.Dispose();
            errorRead.Dispose();
            throw new InvalidOperationException($"The command could not be put in its job ({failure}), so it was not started.");
        }

        WindowsJobSessionAnchor.NativeMethods.CloseHandle(info.Thread);
        var process = info.Process;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new ContainedRun(
            new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8),
            new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8),
            cancellationToken => exited.Task.WaitAsync(cancellationToken),
            () => NativeMethods.GetExitCodeProcess(process, out var code) && code != StillActive ? (int)code : null,
            () =>
            {
                WindowsJobSessionAnchor.NativeMethods.TerminateJobObject(job, exitCode: 1);
                WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
                registration.Unregister(null);
                signal.Dispose();
                WindowsJobSessionAnchor.NativeMethods.CloseHandle(process);
            });
    }

    private static IntPtr _KillOnCloseJob()
    {
        var job = WindowsJobSessionAnchor.NativeMethods.CreateJobObjectW(IntPtr.Zero, null!);
        var limits = new WindowsJobSessionAnchor.NativeMethods.ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = WindowsJobSessionAnchor.NativeMethods.JobObjectLimitKillOnJobClose;
        if (job != IntPtr.Zero && WindowsJobSessionAnchor.NativeMethods.SetInformationJobObject(
                job, WindowsJobSessionAnchor.NativeMethods.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(limits)))
        {
            return job;
        }

        var failure = Marshal.GetLastWin32Error();
        if (job != IntPtr.Zero)
        {
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
        }

        throw new InvalidOperationException($"No job could be created for the command ({failure}), so it was not started.");
    }

    // Only the child's end is inheritable; ours stays private to this process.
    private static (SafeFileHandle Read, SafeFileHandle Write) _Pipe(bool childReads = false)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!NativeMethods.CreatePipe(out var read, out var write, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeMethods.SetHandleInformation(childReads ? write : read, HandleFlagInherit, 0);
        return (read, write);
    }

    private static byte[] _EnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (variable, value) in overrides)
        {
            variables[variable] = value;
        }

        var builder = new StringBuilder();
        foreach (var (variable, value) in variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(variable).Append('=').Append(value).Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.Append('\0').ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(
            string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool bInheritHandles,
            uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    }
}
