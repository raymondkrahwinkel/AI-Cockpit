using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

// AC-1820: report new top-level directories while agents live; never attribute authorship or remove anything.
internal sealed class HomeFolderWatcher(ILogger<HomeFolderWatcher> logger, IToastService? toasts = null)
    : ISingletonService, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<ISessionHandle> _followed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _reported = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ISessionRegistry? _registry;
    private IDelegationService? _delegation;
    private bool _disposed;
    private bool _watchersWanted;
    private bool _watching;
    private bool _workerRunning;
    private readonly HashSet<string> _failedRoots = new(StringComparer.OrdinalIgnoreCase);

    public void Start(ISessionRegistry registry, IDelegationService? delegation = null)
    {
        lock (_gate)
        {
            if (_disposed || _registry is not null)
            {
                return;
            }

            _registry = registry;
            _delegation = delegation;
            registry.Changed += _OnRegistryChanged;
            if (delegation is not null)
            {
                delegation.TasksChanged += _Refresh;
            }

        }

        _Refresh();
    }

    private void _OnRegistryChanged(object? sender, EventArgs e) => _Refresh();

    private void _OnLiveStateChanged(SessionLiveState state) => _Refresh();

    private IReadOnlyList<string> _SessionNames()
    {
        var handles = _registry?.All.ToList() ?? [];
        if (_registry?.Assistant is { } assistant)
        {
            handles.Add(assistant);
        }

        var names = handles
            .Where(handle => !handle.IsTerminal && handle is not IHostedSession { AwaitsStart: true })
            .Select(handle => $"{handle.Title} ({handle.PaneId})").ToList();
        if (_delegation is ILiveSessionSource source)
        {
            foreach (var id in source.LiveSessionIds)
            {
                var task = _delegation.GetTask(id);
                names.Add($"{task?.Label ?? task?.ProfileLabel ?? id} ({id})");
            }
        }

        return names;
    }

    private void _Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var handles = _registry?.All.ToList() ?? [];
            if (_registry?.Assistant is { } assistant)
            {
                handles.Add(assistant);
            }

            foreach (var gone in _followed.Where(handle => !handles.Contains(handle)).ToList())
            {
                gone.LiveStateChanged -= _OnLiveStateChanged;
                _followed.Remove(gone);
            }

            foreach (var handle in handles.Where(handle => !handle.IsTerminal && _followed.Add(handle)))
            {
                handle.LiveStateChanged += _OnLiveStateChanged;
            }

            _watchersWanted = _SessionNames().Count > 0;
            _ScheduleWorker();
        }
    }

    internal static bool ShouldWatchDrive(DriveType type) => true;

    private void _ScheduleWorker()
    {
        if (!_workerRunning && _watchersWanted != _watching)
        {
            _workerRunning = true;
            _ = Task.Run(_UpdateWatchers);
        }
    }

    private void _UpdateWatchers()
    {
        try
        {
            while (true)
            {
                bool wanted;
                List<FileSystemWatcher> retired;
                lock (_gate)
                {
                    wanted = _watchersWanted;
                    if (wanted == _watching)
                    {
                        _workerRunning = false;
                        return;
                    }

                    retired = _watchers.ToList();
                    _watchers.Clear();
                }

                foreach (var watcher in retired)
                {
                    watcher.Dispose();
                }

                var created = wanted ? _CreateWatchers() : [];
                lock (_gate)
                {
                    _watchers.AddRange(created);
                    _watching = wanted;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not update home folder watchers.");
            lock (_gate)
            {
                _workerRunning = false;
            }
        }
    }

    private List<FileSystemWatcher> _CreateWatchers()
    {
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (ShouldWatchDrive(drive.DriveType))
                {
                    roots.Add(drive.Name);
                }
            }
        }

        var created = new List<FileSystemWatcher>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_failedRoots.Contains(root))
            {
                continue;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName,
                };
                watcher.Created += _OnCreated;
                watcher.Renamed += _OnCreated;
                watcher.Error += _OnError;
                watcher.EnableRaisingEvents = true;
                created.Add(watcher);
            }
            catch (Exception exception)
            {
                watcher?.Dispose();
                _failedRoots.Add(root);
                logger.LogWarning(exception, "Could not watch directory {Path}.", root);
            }
        }

        return created;
    }

    private void _OnCreated(object sender, FileSystemEventArgs e) => ReportCreated(e.FullPath);

    // Kept synchronous so the event's session snapshot and notification cannot outlive shutdown.
    internal void ReportCreated(string path)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || Path.GetFileName(path).StartsWith('.') || !Directory.Exists(path))
                {
                    return;
                }

                var names = _SessionNames();
                var canonical = Path.GetFullPath(path);
                if (names.Count == 0 || !_reported.Add(canonical))
                {
                    return;
                }

                var keyPath = OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
                toasts?.Show($"New directory: {canonical}\nSessions running at the time: {string.Join(", ", names)}",
                    ToastSeverity.Warning, [], false, "Cockpit", $"home-folder:{keyPath}", "New folder outside session scratch");
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not report new directory {Path}.", path);
        }
    }

    private void _OnError(object sender, ErrorEventArgs e) =>
        logger.LogWarning(e.GetException(), "Home folder watcher lost filesystem events.");

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_registry is not null)
            {
                _registry.Changed -= _OnRegistryChanged;
            }

            if (_delegation is not null)
            {
                _delegation.TasksChanged -= _Refresh;
            }

            _watchersWanted = false;
            _ScheduleWorker();
            foreach (var handle in _followed)
            {
                handle.LiveStateChanged -= _OnLiveStateChanged;
            }

            _followed.Clear();
        }
    }
}
