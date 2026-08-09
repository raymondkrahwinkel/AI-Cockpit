using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Windows `ISessionMemoryLimiter` (AC-661): one Job Object per session, `JOB_OBJECT_LIMIT_JOB_MEMORY` over the
// whole job, which every process the session spawns joins automatically. Over the limit Windows fails the
// allocation rather than killing outright — the offender normally dies of it, scoped to the job the cockpit is
// not in. No `JOB_OBJECT_LIMIT_BREAKAWAY_OK`, so the cap has no escape hatch; the cost is that a spawn using
// `CREATE_BREAKAWAY_FROM_JOB` fails, which is nothing an agent CLI or its build tools do.
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobMemoryLimiter(ILogger<WindowsJobMemoryLimiter> logger) : ISessionMemoryLimiter
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitJobMemory = 0x00000200;

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    public IDisposable? Apply(int processId, long capBytes)
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            logger.LogWarning("Session memory cap: CreateJobObject failed ({Error}); session {ProcessId} runs uncapped.", Marshal.GetLastWin32Error(), processId);
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = { LimitFlags = JobObjectLimitJobMemory },
            JobMemoryLimit = (UIntPtr)capBytes,
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(job, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                logger.LogWarning("Session memory cap: SetInformationJobObject failed ({Error}); session {ProcessId} runs uncapped.", Marshal.GetLastWin32Error(), processId);
                NativeMethods.CloseHandle(job);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // PROCESS_TERMINATE alongside PROCESS_SET_QUOTA is what AssignProcessToJobObject requires.
        var process = NativeMethods.OpenProcess(ProcessSetQuota | ProcessTerminate, bInheritHandle: false, processId);
        if (process == IntPtr.Zero || !NativeMethods.AssignProcessToJobObject(job, process))
        {
            logger.LogWarning("Session memory cap: AssignProcessToJobObject failed ({Error}); session {ProcessId} runs uncapped.", Marshal.GetLastWin32Error(), processId);
            if (process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(process);
            }

            NativeMethods.CloseHandle(job);
            return null;
        }

        NativeMethods.CloseHandle(process);
        logger.LogInformation("Session {ProcessId} capped at {CapBytes} bytes by a job object.", processId, capBytes);
        return new JobHandle(job);
    }

    // No kill-on-close flag is set, so releasing this never takes a running session with it.
    private sealed class JobHandle(IntPtr job) : IDisposable
    {
        private IntPtr _job = job;

        public void Dispose()
        {
            if (_job != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
