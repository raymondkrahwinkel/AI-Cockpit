using System.Globalization;
using System.Runtime.InteropServices;
using Cockpit.Core.Abstractions;
using Cockpit.Core.HeavyRuns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.HeavyRuns;

// AC-1535: samples the whole machine's CPU and memory every two seconds and smooths them over about thirty, the
// `WorktreeReconciler` timer pattern. Containers need no reading of their own: their load is the machine's, natively
// on Linux and as the WSL2 VM on Windows. macOS has no CPU reading here yet; memory still counts there.
public sealed class MachineResources : IMachineResources, ISingletonService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiskInterval = TimeSpan.FromSeconds(60);

    // An exponential moving average whose weight matches a thirty-second window of two-second samples.
    private const double Smoothing = 2.0 / 30.0;

    private readonly ILogger<MachineResources> _logger;
    private readonly IHeavyRunSettingsStore? _settingsStore;
    private readonly Dictionary<string, DiskSpaceForecast> _diskForecasts = new(StringComparer.Ordinal);
    private IReadOnlyList<DiskSpaceSnapshot> _disks = [];
    private DateTimeOffset? _lastDiskSample;
    private ITimer? _timer;
    private CpuTimes? _lastCpu;
    private bool _isCpuUnmeasured;
    private bool _hasCpu;
    private bool _disposed;

    // An int, not a bool: the timer can fire again while a slow tick is still running on another thread.
    private int _sampling;

    public MachineResources(ILogger<MachineResources>? logger = null, IHeavyRunSettingsStore? settingsStore = null)
    {
        _logger = logger ?? NullLogger<MachineResources>.Instance;
        _settingsStore = settingsStore;
    }

    public MachineLoad Current { get; private set; } = MachineLoad.Unknown;

    public IReadOnlyList<DiskSpaceSnapshot> Disks => Volatile.Read(ref _disks);

    public event Action? Sampled;

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = TimeProvider.System.CreateTimer(_ => _ = _OnTickAsync(), null, TimeSpan.Zero, Interval);
    }

    public long? FreeDiskBytes(string directory)
    {
        try
        {
            var volume = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(directory)) : directory;
            return volume is { Length: > 0 } ? new DriveInfo(volume).AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task _OnTickAsync()
    {
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _Sample();
            await _SampleDisksAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            Sampled?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Sampling the machine's load failed; the next sample will try again.");
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private async Task _SampleDisksAsync(DateTimeOffset timestamp)
    {
        if (_lastDiskSample is { } previous && timestamp - previous < DiskInterval)
        {
            return;
        }

        _lastDiskSample = timestamp;
        var settings = _settingsStore is null ? new HeavyRunSettings()
            : await _settingsStore.LoadAsync().ConfigureAwait(false);
        List<DiskSpaceSnapshot> snapshots = [];
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || FreeDiskBytes(drive.Name) is not { } freeBytes)
                {
                    continue;
                }

                if (!_diskForecasts.TryGetValue(drive.Name, out var forecast))
                {
                    forecast = new DiskSpaceForecast(drive.Name);
                    _diskForecasts.Add(drive.Name, forecast);
                }

                snapshots.Add(forecast.Sample(timestamp, drive.TotalSize, freeBytes, settings));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                _logger.LogDebug(exception, "Reading drive {Drive} failed; the next disk sample will try again.", drive.Name);
            }
        }

        foreach (var name in _diskForecasts.Keys.Except(snapshots.Select(snapshot => snapshot.Name)).ToArray())
        {
            _diskForecasts.Remove(name);
        }

        Volatile.Write(ref _disks, snapshots.AsReadOnly());
    }

    private void _Sample()
    {
        var cpu = _ReadCpuPercent();
        var (available, total) = _ReadMemory();
        var previous = Current;

        // The first reading of each is taken as is: averaging up from zero reads an idle machine for half a minute.
        var cpuPercent = cpu is not { } measured ? previous.CpuPercent
            : _hasCpu ? previous.CpuPercent + (Smoothing * (measured - previous.CpuPercent))
            : measured;
        _hasCpu |= cpu is not null;
        var availableBytes = previous.TotalMemoryBytes == 0 ? available
            : (long)(previous.AvailableMemoryBytes + (Smoothing * (available - previous.AvailableMemoryBytes)));
        Current = new MachineLoad(cpuPercent, availableBytes, total);
    }

    private double? _ReadCpuPercent()
    {
        CpuTimes? now = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                now = CpuTimes.FromSystemTimes(idle, kernel, user);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            using var reader = new StreamReader("/proc/stat");
            now = CpuTimes.FromProcStat(reader.ReadLine() ?? string.Empty);
        }
        else if (!_isCpuUnmeasured)
        {
            _isCpuUnmeasured = true;
            _logger.LogInformation("Machine-wide CPU is not measured on this platform; heavy runs are admitted on memory alone.");
        }

        var before = _lastCpu;
        _lastCpu = now;
        return before is { } start && now is { } end ? CpuTimes.BusyPercent(start, end) : null;
    }

    private static (long Available, long Total) _ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                return ((long)status.AvailablePhysical, (long)status.TotalPhysical);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var fields = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim().Split(' ')[0], StringComparer.Ordinal);
            if (fields.TryGetValue("MemAvailable", out var availableKb) && fields.TryGetValue("MemTotal", out var totalKb))
            {
                return (long.Parse(availableKb, CultureInfo.InvariantCulture) * 1024, long.Parse(totalKb, CultureInfo.InvariantCulture) * 1024);
            }
        }

        var info = GC.GetGCMemoryInfo();
        return (Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes), info.TotalAvailableMemoryBytes);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
