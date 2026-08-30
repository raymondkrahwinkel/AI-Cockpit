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

    public IDisposable? Anchor(int processId)
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
            sessionStartedAt);
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, record.JobName);
        if (job == IntPtr.Zero)
        {
            logger.LogWarning("Session {ProcessId}: CreateJobObject failed ({Error}).", processId, Marshal.GetLastWin32Error());
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

            // A Cockpit crash must leave agents running; only an explicit session close ends this job.
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
    DateTimeOffset RootStartedAt);

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

    internal sealed record SweepOutcome(int Terminated, int AlreadyGone, int SkippedForLiveOwner, int SkippedForPidReuse, IReadOnlyList<string> CompletedJobs);

    public static void Run(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            _Run(logger);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void _Run(ILogger logger)
    {
        var registry = new WindowsJobSessionRegistry(Path.Combine(CockpitConfigPath.Root, "session-jobs.json"), logger);
        var outcome = Sweep(registry.Load(), WindowsJobSessionAnchor.StartedAt, Terminate);
        foreach (var jobName in outcome.CompletedJobs)
        {
            registry.Remove(jobName);
        }

        if (outcome.Terminated > 0)
        {
            logger.LogWarning(
                "Stopped {Processes} leftover Windows session job(s) from a previous Cockpit run.", outcome.Terminated);
        }
    }

    internal static SweepOutcome Sweep(
        IReadOnlyList<WindowsJobSessionRecord> records,
        Func<int, DateTimeOffset?> processStartedAt,
        Func<string, JobTermination> terminate)
    {
        var terminated = 0;
        var alreadyGone = 0;
        var liveOwners = 0;
        var reusedPids = 0;
        var completed = new List<string>();

        foreach (var record in records)
        {
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

            switch (terminate(record.JobName))
            {
                case JobTermination.Terminated:
                    terminated++;
                    completed.Add(record.JobName);
                    break;
                case JobTermination.AlreadyGone:
                    alreadyGone++;
                    completed.Add(record.JobName);
                    break;
            }
        }

        return new SweepOutcome(terminated, alreadyGone, liveOwners, reusedPids, completed);
    }

    [SupportedOSPlatform("windows")]
    internal static JobTermination Terminate(string jobName)
    {
        var job = WindowsJobSessionAnchor.NativeMethods.OpenJobObjectW(JobObjectTerminate, inheritHandle: false, jobName);
        if (job == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == 2 ? JobTermination.AlreadyGone : JobTermination.Failed;
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
}
