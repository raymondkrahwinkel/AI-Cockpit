using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.App.Services;

// AC-1013: AC-185's dev inner loop — watches `plugins-dev` for a rebuilt first-party plugin and offers a
// toast to bring it into the running sandbox instead of a manual restart (DEBUG-only). Not a hot-swap: it
// reruns `DevPluginInstaller.InstallAsync` then restarts via `IAppRestartService`; watch and debounce are injectable.
public sealed class DevPluginReloadWatcher : ISingletonService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);

    private readonly Func<string?> _resolvePluginsDevRoot;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _installAsync;
    private readonly IToastService _toast;
    private readonly IAppRestartService _restartService;
    private readonly ILogger<DevPluginReloadWatcher> _logger;
    private readonly Action<Action> _debounce;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;
    private bool _disposed;

    public DevPluginReloadWatcher(IToastService toast, IAppRestartService restartService, ILogger<DevPluginReloadWatcher> logger)
        : this(
            DevPluginInstaller.FindPluginsDevRoot,
            cancellationToken => new DevPluginInstaller(logger).InstallAsync(PluginBootstrap.PluginsRoot, cancellationToken),
            toast,
            restartService,
            logger,
            debounce: null)
    {
    }

    internal DevPluginReloadWatcher(
        Func<string?> resolvePluginsDevRoot,
        Func<CancellationToken, Task<IReadOnlyList<string>>> installAsync,
        IToastService toast,
        IAppRestartService restartService,
        ILogger<DevPluginReloadWatcher> logger,
        Action<Action>? debounce)
    {
        _resolvePluginsDevRoot = resolvePluginsDevRoot;
        _installAsync = installAsync;
        _toast = toast;
        _restartService = restartService;
        _logger = logger;
        _debounce = debounce ?? _DebounceOnUiThread;
    }

    // Starts watching, if this is a dev checkout. A second call, or one after `Dispose`, is ignored.
    public void Start()
    {
        if (_watcher is not null || _disposed)
        {
            return;
        }

        if (_resolvePluginsDevRoot() is not { } pluginsDevRoot)
        {
            return; // not a dev checkout — nothing to watch
        }

        var watcher = new FileSystemWatcher(pluginsDevRoot)
        {
            IncludeSubdirectories = true,
            Filter = "*.dll",
            NotifyFilter = NotifyFilters.LastWrite,
        };
        watcher.Changed += _OnBuildOutputChanged;
        watcher.Created += _OnBuildOutputChanged;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        _logger.LogInformation("Watching {Root} for a rebuilt plugin.", pluginsDevRoot);
    }

    // Fires once per changed/created .dll under a build's output, which for one `dotnet build` is several — the
    // debounce below coalesces those into a single settle. Only bin/ counts: obj/ churns for the whole build and
    // is never what gets installed from (see DevPluginInstaller's own source-folder resolution).
    private void _OnBuildOutputChanged(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _debounce(_OnSettled);
    }

    // Every further rebuild event while this toast is still up needs no toast of its own — its action installs
    // whatever is on disk the moment it is clicked, not whatever triggered this particular pass — so nothing here
    // deduplicates across settles; the debounce upstream already collapses one build's own burst of writes.
    private void _OnSettled() =>
        _toast.Show("A dev plugin was rebuilt.", ToastSeverity.Information, "Reload", () => _ = _ReloadAsync());

    private async Task _ReloadAsync()
    {
        try
        {
            await _installAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The toast's click ran on the UI thread, so this continuation is still on it (no
            // ConfigureAwait(false) above) — Restart() below needs that, and so does this toast.
            _logger.LogWarning(exception, "Reloading the dev plugin failed; the cockpit was not restarted.");
            _toast.Show("Reloading the dev plugin failed — check the log.", ToastSeverity.Error);
            return;
        }

        _restartService.Restart();
    }

    // AC-1013: marshals onto the UI thread (FileSystemWatcher raises off a thread-pool thread), then
    // restarts one shared timer, because DispatcherTimer is tied to the dispatcher of the thread that creates it.
    private void _DebounceOnUiThread(Action callback)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (_debounceTimer is null)
            {
                _debounceTimer = new DispatcherTimer { Interval = DebounceDelay };
                _debounceTimer.Tick += (_, _) =>
                {
                    _debounceTimer!.Stop();
                    callback();
                };
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }

    // Simulates a build-output write for a test, bypassing the real `FileSystemWatcher`.
    internal void SimulateBuildOutputChangedForTests(string fullPath) =>
        _OnBuildOutputChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fullPath) ?? ".", Path.GetFileName(fullPath)));

    public void Dispose()
    {
        _disposed = true;

        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= _OnBuildOutputChanged;
            watcher.Created -= _OnBuildOutputChanged;
            watcher.Dispose();
            _watcher = null;
        }

        if (_debounceTimer is { } timer)
        {
            timer.Stop();
            _debounceTimer = null;
        }
    }
}
