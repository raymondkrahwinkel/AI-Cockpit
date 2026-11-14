using Cockpit.Core.Configuration;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugin.Browser.Host;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal enum BrowserEngineState
{
    NotStarted,
    Fetching,
    Starting,
    Ready,
    Unavailable,
}

// One runtime-owned host, started on first opening. Native CEF stays in the child; a later enable gets a fresh engine.
internal sealed class BrowserEngine
{
    public const string SubprocessFileName = "Cockpit.Plugin.Browser.Subprocess";

    // Its own folder (AC-1740): in the store zip it is a self-contained publish with a runtime of its own.
    public const string SubprocessFolder = "Subprocess";

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(5);

    private readonly Func<IProgress<CefRuntimeProgress>, CancellationToken, Task<CefRuntimeResult>> _fetchRuntime;
    private readonly string _subprocessPath;
    private readonly string _rootCachePath;
    private readonly List<BrowserView> _views = [];
    private readonly List<Task> _closing = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _start;
    private Task _hostStopped = Task.CompletedTask;
    private BrowserHostClient? _host;

    public BrowserEngine(
        Func<IProgress<CefRuntimeProgress>, CancellationToken, Task<CefRuntimeResult>> fetchRuntime,
        string subprocessPath,
        string rootCachePath)
    {
        _fetchRuntime = fetchRuntime;
        _subprocessPath = subprocessPath;
        _rootCachePath = rootCachePath;
    }

    // A headless scene's engine: held in one state, never fetching, never touching CEF.
    internal static BrowserEngine ForScene(BrowserEngineState state, string? reason = null, CefRuntimeProgress? progress = null, string? downloadText = null) =>
        new((_, _) => new TaskCompletionSource<CefRuntimeResult>().Task, string.Empty, string.Empty)
        {
            State = state,
            Reason = reason,
            Progress = progress,
            DownloadText = downloadText,
            _start = Task.CompletedTask,
        };

    internal static BrowserEngine ForHost(BrowserHostClient host) => new((_, _) => throw new InvalidOperationException("The host is already running."), string.Empty, string.Empty)
    {
        _host = host,
        _start = Task.CompletedTask,
        State = BrowserEngineState.Ready,
    };

    internal BrowserView CreateView(string address)
    {
        if (_stopping.IsCancellationRequested || _host is not { } host)
        {
            throw new InvalidOperationException("The browser host is not running.");
        }
        var view = new HostBrowserView(host, address);
        _views.Add(view);
        view.Closing += closed =>
        {
            _views.Remove(view);
            Closing(closed);
        };
        return view;
    }

    internal static BrowserEngine Create() => new(
        (progress, token) =>
        {
            var catalog = CefRuntimeCatalog.Load();
            var rid = CefRuntimeCatalog.CurrentRid();
            return rid is null
                ? Task.FromResult(new CefRuntimeResult(null, "CEF publishes no browser engine for this processor."))
                : new CefRuntimeCache(CefRuntimeCache.DefaultRoot).EnsureAvailableAsync(catalog, rid, progress, token);
        },
        Path.Combine(Path.GetDirectoryName(typeof(BrowserEngine).Assembly.Location) ?? AppContext.BaseDirectory, SubprocessFolder, _SubprocessExecutable()),
        Path.Combine(CockpitBuild.StateRoot, "browser"))
    {
        DownloadText = CefRuntimeCatalog.CurrentRid() is { } rid && CefRuntimeCatalog.Load().Packages.TryGetValue(rid, out var package)
            ? package.DownloadText
            : null,
    };

    // What the download costs, shown while it runs (AC-1727); null where there is nothing to download.
    public string? DownloadText { get; init; }

    internal void ValidatePackage()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }
        string folder = Path.GetDirectoryName(_subprocessPath) ?? throw new IOException("The browser subprocess folder is missing.");
        foreach (string file in new[] { _subprocessPath, Path.Combine(folder, SubprocessFileName + ".dll"),
            Path.Combine(folder, SubprocessFileName + ".deps.json"), Path.Combine(folder, SubprocessFileName + ".runtimeconfig.json"),
            Path.Combine(folder, "Xilium.CefGlue.dll") })
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("The browser subprocess package is incomplete: " + Path.GetFileName(file), file);
            }
        }
    }

    public bool IsHardwareAccelerationEnabled { get; set; } = !OperatingSystem.IsLinux();

    public BrowserEngineState State { get; private set; } = BrowserEngineState.NotStarted;

    public string? Reason { get; private set; }

    public CefRuntimeProgress? Progress { get; private set; }

    public event EventHandler? Changed;

    // AC-1869: move the profile from its "raymond" folder to "user" once, keeping its logins and cookies.
    internal static string MigrateProfileCachePath(string rootCachePath)
    {
        var user = Path.Combine(rootCachePath, "user");
        if (!Directory.Exists(user))
        {
            var old = Path.Combine(rootCachePath, "raymond");
            if (Directory.Exists(old))
            {
                Directory.Move(old, user);
            }
        }

        return user;
    }

    // Idempotent; completes when the engine is Ready or Unavailable, and never throws.
    public Task EnsureStartedAsync() => _start ??= _StartAsync();

    // A failed start can retry with a fresh child; the UI process never loads CEF.
    public bool CanRetry => State == BrowserEngineState.Unavailable && _stopping.IsCancellationRequested == false
        && OperatingSystem.IsMacOS() == false;

    public Task RetryAsync()
    {
        if (CanRetry)
        {
            _start = null;
        }

        return EnsureStartedAsync();
    }

    private async Task _StartAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            _Set(BrowserEngineState.Unavailable, "The browser is off on macOS until it has been tried on a Mac (AC-1711): CEF there needs an app bundle with helper apps and a signed framework.");
            return;
        }

        try
        {
            _Set(BrowserEngineState.Fetching, null);
            var progress = new Progress<CefRuntimeProgress>(value =>
            {
                Progress = value;
                Changed?.Invoke(this, EventArgs.Empty);
            });
            var runtime = await _fetchRuntime(progress, _stopping.Token);
            if (_stopping.IsCancellationRequested)
            {
                _Set(BrowserEngineState.Unavailable, "The browser engine has shut down.");
                return;
            }

            if (runtime.Directory is null)
            {
                _Set(BrowserEngineState.Unavailable, runtime.Failure ?? "The browser engine could not be fetched.");
                return;
            }

            _Set(BrowserEngineState.Starting, null);
            MigrateProfileCachePath(_rootCachePath);
            _host = await BrowserHostClient.StartAsync(_subprocessPath, runtime.Directory, _rootCachePath,
                Path.Combine(_rootCachePath, "ipc"), IsHardwareAccelerationEnabled, _stopping.Token);
            if (_stopping.IsCancellationRequested)
            {
                await _host.DisposeAsync();
                _host = null;
                return;
            }
            _hostStopped = _HostStoppedAsync(_host);
            _Set(BrowserEngineState.Ready, null);
        }
        catch (OperationCanceledException)
        {
            _Set(BrowserEngineState.Unavailable, "The browser engine has shut down.");
        }
        catch (Exception exception)
        {
            _Set(BrowserEngineState.Unavailable, $"The browser engine could not start: {exception.Message}");
        }
    }

    private async Task _HostStoppedAsync(BrowserHostClient host)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.Connection.Closed.Register(() => stopped.TrySetResult());
        await stopped.Task;
        if (_stopping.IsCancellationRequested == false && ReferenceEquals(_host, host))
        {
            _host = null;
            await _StopHostAsync(host);
            if (_stopping.IsCancellationRequested == false)
            {
                _Set(BrowserEngineState.Unavailable, "The browser engine stopped. Retry to start a new host.");
            }
        }
    }

    // No RemoteDebuggingPort, ever: it opens every tab of every profile in the process to anyone on the machine
    // (AC-1709, measured). Reading and driving a tab goes in-process, through ExecuteDevToolsMethod.
    internal static CefSettings CreateSettings(string runtimeDir, string subprocessPath, string rootCachePath) =>
        BrowserHostRuntime.CreateSettings(runtimeDir, subprocessPath, rootCachePath);

    internal IReadOnlyList<KeyValuePair<string, string?>> Switches => BrowserHostRuntime.Switches(IsHardwareAccelerationEnabled);

    internal async Task<BrowserHostClient> StartHostAsync(CancellationToken cancellationToken = default)
    {
        var progress = new Progress<CefRuntimeProgress>(value =>
        {
            Progress = value;
            Changed?.Invoke(this, EventArgs.Empty);
        });
        var runtime = await _fetchRuntime(progress, cancellationToken);
        if (runtime.Directory is null)
        {
            throw new IOException(runtime.Failure ?? "The browser engine could not be fetched.");
        }
        return await BrowserHostClient.StartAsync(_subprocessPath, runtime.Directory, _rootCachePath,
            Path.Combine(_rootCachePath, "ipc"), IsHardwareAccelerationEnabled, cancellationToken);
    }

    public void Register(CefBrowserView view) => _views.Add(view);

    public void Unregister(CefBrowserView view) => _views.Remove(view);

    // Teardown never waits on the dispatcher: tab callbacks and a cancelled startup still need it to finish.
    public void Shutdown()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }
        _stopping.Cancel();
        _host?.Connection.Close();
        foreach (var view in _views.ToArray())
        {
            _ = view.CloseAsync();
        }
        _views.Clear();
        _Set(BrowserEngineState.Unavailable, "The browser engine has shut down.");
        ShutdownCompletion = _StopHostAsync();
    }

    internal Task ShutdownCompletion { get; private set; } = Task.CompletedTask;

    private async Task _StopHostAsync()
    {
        try
        {
            if (_start is not null)
            {
                await _start;
            }
            await _hostStopped;
            try
            {
                await Task.WhenAll(_closing).WaitAsync(CloseBudget);
            }
            finally
            {
                if (_host is { } host)
                {
                    _host = null;
                    await _StopHostAsync(host).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            _LogLine($"Browser host cleanup failed: {exception.Message}");
            throw;
        }
    }

    private async Task _StopHostAsync(BrowserHostClient host)
    {
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _LogLine($"Browser host cleanup failed: {exception.Message}");
            throw;
        }
    }

    // A tab closed by hand may still have callbacks finishing when teardown starts.
    public void Closing(Task closed)
    {
        _closing.RemoveAll(task => task.IsCompleted);
        _closing.Add(closed);
    }

    // The engine's own trace next to CEF's log; the UI part has no logger of the host's.
    private void _LogLine(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(_rootCachePath, "engine.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void _Set(BrowserEngineState state, string? reason)
    {
        State = state;
        Reason = reason;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string _SubprocessExecutable() => OperatingSystem.IsWindows() ? SubprocessFileName + ".exe" : SubprocessFileName;

}
