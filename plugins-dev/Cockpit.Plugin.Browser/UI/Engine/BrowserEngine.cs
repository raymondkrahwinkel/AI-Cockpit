using System.Runtime.InteropServices;
using Avalonia.Threading;
using Cockpit.Core.Configuration;
using Cockpit.Plugin.Browser.UI.Runtime;
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

// The one CEF of this process (AC-1711). Started on the first opening, not with Cockpit; shut down on the UI thread
// after every browser has closed. Any failure becomes Unavailable with a reason: the panel shows it, Cockpit stays up.
internal sealed class BrowserEngine
{
    public const string SubprocessFileName = "Cockpit.Plugin.Browser.Subprocess";

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(2);

    private static string _libCefPath = string.Empty;
    private static int _resolverSet;

    private readonly Func<IProgress<CefRuntimeProgress>, CancellationToken, Task<CefRuntimeResult>> _fetchRuntime;
    private readonly string _subprocessPath;
    private readonly string _rootCachePath;
    private readonly List<CefBrowserView> _views = [];
    private Task? _start;
    private CefRequestContext? _raymondProfile;

    public BrowserEngine(
        Func<IProgress<CefRuntimeProgress>, CancellationToken, Task<CefRuntimeResult>> fetchRuntime,
        string subprocessPath,
        string rootCachePath)
    {
        _fetchRuntime = fetchRuntime;
        _subprocessPath = subprocessPath;
        _rootCachePath = rootCachePath;
    }

    public static BrowserEngine Shared { get; } = new(
        (progress, token) =>
        {
            var catalog = CefRuntimeCatalog.Load();
            var rid = CefRuntimeCatalog.CurrentRid();
            return rid is null
                ? Task.FromResult(new CefRuntimeResult(null, "CEF publishes no browser engine for this processor."))
                : new CefRuntimeCache(CefRuntimeCache.DefaultRoot).EnsureAvailableAsync(catalog, rid, progress, token);
        },
        Path.Combine(Path.GetDirectoryName(typeof(BrowserEngine).Assembly.Location) ?? AppContext.BaseDirectory, _SubprocessExecutable()),
        Path.Combine(CockpitBuild.StateRoot, "browser"));

    public BrowserEngineState State { get; private set; } = BrowserEngineState.NotStarted;

    public string? Reason { get; private set; }

    public CefRuntimeProgress? Progress { get; private set; }

    public event EventHandler? Changed;

    // Raymond's own profile: persistent, under the root cache, apart from any agent profile (AC-1712).
    public CefRequestContext RaymondProfile => _raymondProfile ??= CefRequestContext.CreateContext(
        new CefRequestContextSettings { CachePath = Path.Combine(_rootCachePath, "raymond"), PersistSessionCookies = true },
        null);

    // Idempotent; completes when the engine is Ready or Unavailable, and never throws.
    public Task EnsureStartedAsync() => _start ??= _StartAsync();

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
            var runtime = await _fetchRuntime(progress, CancellationToken.None);
            if (runtime.Directory is null)
            {
                _Set(BrowserEngineState.Unavailable, runtime.Failure ?? "The browser engine could not be fetched.");
                return;
            }

            _Set(BrowserEngineState.Starting, null);
            _Initialize(runtime.Directory);
            _Set(BrowserEngineState.Ready, null);
        }
        catch (Exception exception)
        {
            _Set(BrowserEngineState.Unavailable, $"The browser engine could not start: {exception.Message}");
        }
    }

    private void _Initialize(string runtimeDir)
    {
        var subprocess = _subprocessPath;
        if (File.Exists(subprocess) == false)
        {
            throw new FileNotFoundException($"The browser subprocess is missing from the plugin ({Path.GetFileName(subprocess)}).");
        }

        _libCefPath = Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "libcef.dll" : "libcef.so");
        if (Interlocked.Exchange(ref _resolverSet, 1) == 0)
        {
            // Once per assembly: a second resolver throws. libcef sits in the runtime cache, outside the plugin folder.
            NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
                name == "libcef" ? NativeLibrary.Load(_libCefPath) : nint.Zero);
        }

        Environment.SetEnvironmentVariable("COCKPIT_CEF_RUNTIME_DIR", runtimeDir);
        _CreateOwnerOnly(_rootCachePath);

        CefRuntime.Load(runtimeDir);
        CefRuntime.Initialize(new CefMainArgs([]), CreateSettings(runtimeDir, subprocess, _rootCachePath), new BrowserApp(), nint.Zero);
    }

    // No RemoteDebuggingPort, ever: it opens every tab of every profile in the process to anyone on the machine
    // (AC-1709, measured). Reading and driving a tab goes in-process, through ExecuteDevToolsMethod.
    internal static CefSettings CreateSettings(string runtimeDir, string subprocessPath, string rootCachePath) => new()
    {
        MultiThreadedMessageLoop = true,
        WindowlessRenderingEnabled = true,
        NoSandbox = true,
        CommandLineArgsDisabled = true,
        BrowserSubprocessPath = subprocessPath,
        RootCachePath = rootCachePath,
        ResourcesDirPath = runtimeDir,
        LocalesDirPath = Path.Combine(runtimeDir, "locales"),
        PersistSessionCookies = true,
        LogSeverity = CefLogSeverity.Warning,
        LogFile = Path.Combine(rootCachePath, "cef.log"),
    };

    // The only switches the browser process gets. GPU stays on: twice as frugal in OSR as the fork's GPU-off set.
    internal static IReadOnlyList<KeyValuePair<string, string?>> Switches { get; } =
    [
        new("autoplay-policy", "no-user-gesture-required"),
        .. OperatingSystem.IsLinux() ? [new KeyValuePair<string, string?>("no-zygote", null)] : Array.Empty<KeyValuePair<string, string?>>(),
    ];

    public void Register(CefBrowserView view) => _views.Add(view);

    public void Unregister(CefBrowserView view) => _views.Remove(view);

    // On the UI thread, after every browser has closed; CEF's own threads answer the close, so waiting here is safe.
    public void Shutdown()
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.InvokeAsync(Shutdown).Wait(CloseBudget + TimeSpan.FromSeconds(1));
            return;
        }

        if (State != BrowserEngineState.Ready)
        {
            return;
        }

        var closing = _views.Select(view => view.CloseAsync()).ToArray();
        Task.WaitAll(closing, CloseBudget);
        _views.Clear();
        _raymondProfile = null;
        CefRuntime.Shutdown();
        _Set(BrowserEngineState.Unavailable, "The browser engine has shut down.");
    }

    private void _Set(BrowserEngineState state, string? reason)
    {
        State = state;
        Reason = reason;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void _CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string _SubprocessExecutable() => OperatingSystem.IsWindows() ? SubprocessFileName + ".exe" : SubprocessFileName;

    private sealed class BrowserApp : CefApp
    {
        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            if (string.IsNullOrEmpty(processType) == false)
            {
                return;
            }

            foreach (var (name, value) in Switches)
            {
                if (value is null)
                {
                    commandLine.AppendSwitch(name);
                }
                else
                {
                    commandLine.AppendSwitch(name, value);
                }
            }
        }
    }
}
