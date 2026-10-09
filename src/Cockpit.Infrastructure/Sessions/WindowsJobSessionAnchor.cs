using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

[SupportedOSPlatform("windows")]
internal sealed class WindowsJobSessionAnchor(WindowsJobSessionRegistry registry, ILogger<WindowsJobSessionAnchor> logger) : ISessionProcessAnchor
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    public IDisposable? Anchor(int processId, string? paneId = null)
    {
        if (_StartedAt(processId) is not { } sessionStartedAt || _StartedAt(Environment.ProcessId) is not { } ownerStartedAt)
        {
            logger.LogWarning("Session {ProcessId}: could not establish process identity for a Windows job anchor.", processId);
            return null;
        }

        var record = new WindowsJobSessionRecord(
            $"cockpit-session-{Guid.NewGuid():N}",
            Environment.ProcessId,
            ownerStartedAt,
            processId,
            sessionStartedAt,
            paneId);
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, record.JobName);
        if (job == IntPtr.Zero)
        {
            logger.LogWarning("Session {ProcessId}: CreateJobObject failed ({Error}).", processId, Marshal.GetLastWin32Error());
            return null;
        }

        // AC-1519: the tree dies with this process however it ends, so a crashed Cockpit leaves no agent
        // driving a branch behind a pane that reads Idle; the startup sweep is the fallback, not the plan.
        if (!_KillOnClose(job))
        {
            logger.LogWarning("Session {ProcessId}: SetInformationJobObject failed ({Error}).", processId, Marshal.GetLastWin32Error());
            NativeMethods.CloseHandle(job);
            return null;
        }

        if (!registry.TryRegister(record))
        {
            NativeMethods.CloseHandle(job);
            return null;
        }

        var process = NativeMethods.OpenProcess(ProcessSetQuota | ProcessTerminate, bInheritHandle: false, processId);
        if (process == IntPtr.Zero || !NativeMethods.AssignProcessToJobObject(job, process))
        {
            logger.LogWarning("Session {ProcessId}: AssignProcessToJobObject failed ({Error}).", processId, Marshal.GetLastWin32Error());
            if (process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(process);
            }

            registry.Remove(record.JobName);
            NativeMethods.CloseHandle(job);
            return null;
        }

        NativeMethods.CloseHandle(process);
        return new JobHandle(job, record.JobName, registry, logger);
    }

    internal static DateTimeOffset? StartedAt(int processId) => _StartedAt(processId);

    private static bool _KillOnClose(IntPtr job)
    {
        var limits = new NativeMethods.ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        return NativeMethods.SetInformationJobObject(
            job, NativeMethods.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<NativeMethods.ExtendedLimitInformation>());
    }

    private static DateTimeOffset? _StartedAt(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class JobHandle(IntPtr job, string jobName, WindowsJobSessionRegistry registry, ILogger logger) : IDisposable
    {
        private IntPtr _job = job;

        public void Dispose()
        {
            if (_job == IntPtr.Zero)
            {
                return;
            }

            if (!NativeMethods.TerminateJobObject(_job, exitCode: 1))
            {
                logger.LogWarning("Session job {Job}: TerminateJobObject failed ({Error}).", jobName, Marshal.GetLastWin32Error());
            }

            NativeMethods.CloseHandle(_job);
            _job = IntPtr.Zero;
            registry.Remove(jobName);
        }
    }

    internal static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimitInformation
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
        public struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenJobObjectW(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

internal sealed record WindowsJobSessionRecord(
    string JobName,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAt,
    int RootProcessId,
    DateTimeOffset RootStartedAt,
    string? PaneId = null);

internal sealed class WindowsJobSessionRegistry
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public WindowsJobSessionRegistry(ILogger<WindowsJobSessionRegistry> logger)
        : this(Path.Combine(CockpitConfigPath.Root, "session-jobs.json"), logger)
    {
    }

    internal WindowsJobSessionRegistry(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public bool TryRegister(WindowsJobSessionRecord record)
    {
        lock (_gate)
        {
            var records = _Load();
            if (records is null)
            {
                return false;
            }

            records.Add(record);
            return _Save(records);
        }
    }

    public IReadOnlyList<WindowsJobSessionRecord> Load()
    {
        lock (_gate)
        {
            return _Load() ?? [];
        }
    }

    public void Remove(string jobName)
    {
        lock (_gate)
        {
            if (_Load() is not { } records)
            {
                return;
            }

            _Save(records.Where(record => record.JobName != jobName).ToList());
        }
    }

    private List<WindowsJobSessionRecord>? _Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WindowsJobSessionRecord>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not read the Windows session-job registry at {Path}.", _path);
            return null;
        }
    }

    private bool _Save(List<WindowsJobSessionRecord> records)
    {
        try
        {
            CockpitConfigPath.ReplaceAtomicallyPrivate(_path, JsonSerializer.Serialize(records));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not write the Windows session-job registry at {Path}.", _path);
            return false;
        }
    }
}

internal static class WindowsJobSessionSweep
{
    private const uint JobObjectTerminate = 0x0008;

    internal enum JobTermination
    {
        Terminated,
        AlreadyGone,
        Failed,
    }

    internal sealed record SweepOutcome(
        int Terminated,
        int AlreadyGone,
        int SkippedForLiveOwner,
        int SkippedForPidReuse,
        IReadOnlyList<string> CompletedJobs,
        IReadOnlyList<string> StoppedPanes);

    // Sweeps every leftover of a previous run, or only `paneId`'s when stop_agent asks; returns the panes it stopped.
    public static IReadOnlyList<string> Run(ILogger logger, string? paneId = null) =>
        OperatingSystem.IsWindows() ? _Run(logger, paneId) : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> _Run(ILogger logger, string? paneId)
    {
        var registry = new WindowsJobSessionRegistry(Path.Combine(CockpitConfigPath.Root, "session-jobs.json"), logger);
        var outcome = Sweep(registry.Load(), WindowsJobSessionAnchor.StartedAt, Terminate, paneId);
        foreach (var jobName in outcome.CompletedJobs)
        {
            registry.Remove(jobName);
        }

        if (outcome.Terminated > 0)
        {
            logger.LogWarning(
                "Stopped {Processes} leftover Windows session job(s) from a previous Cockpit run, for pane(s) {Panes}.",
                outcome.Terminated,
                string.Join(", ", outcome.StoppedPanes));
        }

        return outcome.StoppedPanes;
    }

    // AC-1519: an orphan is a recorded tree whose Cockpit is gone while its root is still the process we anchored.
    internal static SweepOutcome Sweep(
        IReadOnlyList<WindowsJobSessionRecord> records,
        Func<int, DateTimeOffset?> processStartedAt,
        Func<WindowsJobSessionRecord, JobTermination> terminate,
        string? paneId = null)
    {
        var terminated = 0;
        var alreadyGone = 0;
        var liveOwners = 0;
        var reusedPids = 0;
        var completed = new List<string>();
        var stoppedPanes = new List<string>();

        foreach (var record in records)
        {
            if (paneId is not null && record.PaneId != paneId)
            {
                continue;
            }

            if (processStartedAt(record.OwnerProcessId) == record.OwnerStartedAt)
            {
                liveOwners++;
                continue;
            }

            if (processStartedAt(record.RootProcessId) is { } root && root != record.RootStartedAt)
            {
                reusedPids++;
                continue;
            }

            switch (terminate(record))
            {
                case JobTermination.Terminated:
                    terminated++;
                    completed.Add(record.JobName);
                    if (record.PaneId is { } stoppedPane)
                    {
                        stoppedPanes.Add(stoppedPane);
                    }

                    break;
                case JobTermination.AlreadyGone:
                    alreadyGone++;
                    completed.Add(record.JobName);
                    break;
            }
        }

        return new SweepOutcome(terminated, alreadyGone, liveOwners, reusedPids, completed, stoppedPanes);
    }

    [SupportedOSPlatform("windows")]
    internal static JobTermination Terminate(WindowsJobSessionRecord record)
    {
        var job = WindowsJobSessionAnchor.NativeMethods.OpenJobObjectW(JobObjectTerminate, inheritHandle: false, record.JobName);
        if (job == IntPtr.Zero)
        {
            // A job's name dies with the last handle to it, so a crashed Cockpit's job cannot be opened while the tree
            // it held may still run (AC-1519); then the anchored root itself is what is left to end.
            return Marshal.GetLastWin32Error() == 2 ? _KillRoot(record) : JobTermination.Failed;
        }

        try
        {
            return WindowsJobSessionAnchor.NativeMethods.TerminateJobObject(job, exitCode: 1)
                ? JobTermination.Terminated
                : JobTermination.Failed;
        }
        finally
        {
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
        }
    }

    [SupportedOSPlatform("windows")]
    private static JobTermination _KillRoot(WindowsJobSessionRecord record)
    {
        if (WindowsJobSessionAnchor.StartedAt(record.RootProcessId) != record.RootStartedAt)
        {
            return JobTermination.AlreadyGone;
        }

        try
        {
            using var root = Process.GetProcessById(record.RootProcessId);
            root.Kill(entireProcessTree: true);
            return JobTermination.Terminated;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return JobTermination.Failed;
        }
    }
}
