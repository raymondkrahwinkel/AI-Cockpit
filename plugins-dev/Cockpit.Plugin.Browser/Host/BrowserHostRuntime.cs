using System.Runtime.InteropServices;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal static class BrowserHostRuntime
{
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

    internal static IReadOnlyList<KeyValuePair<string, string?>> Switches(bool hardwareAcceleration) =>
    [
        new("autoplay-policy", "no-user-gesture-required"),
        .. OperatingSystem.IsLinux() ? [new KeyValuePair<string, string?>("no-zygote", null)] : Array.Empty<KeyValuePair<string, string?>>(),
        .. hardwareAcceleration ? Array.Empty<KeyValuePair<string, string?>>() :
            [new("disable-gpu", null), new("disable-gpu-compositing", null)],
    ];

    internal static CefApp CreateApp(string runtimeDir, IReadOnlyList<KeyValuePair<string, string?>> switches) => new BrowserApp(runtimeDir, switches);

    internal static void Load(string runtimeDir)
    {
        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
            name == "libcef" ? NativeLibrary.Load(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "libcef.dll" : "libcef.so")) : nint.Zero);
        if (OperatingSystem.IsLinux())
        {
            CefRuntime.Load();
        }
        else
        {
            CefRuntime.Load(runtimeDir);
        }
    }

    private sealed class BrowserApp(string runtimeDir, IReadOnlyList<KeyValuePair<string, string?>> switches) : CefApp
    {
        private readonly BrowserProcessHandler _handler = new(runtimeDir);

        protected override CefBrowserProcessHandler GetBrowserProcessHandler() => _handler;

        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            if (string.IsNullOrEmpty(processType) == false)
            {
                return;
            }
            foreach (var (name, value) in switches)
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

    private sealed class BrowserProcessHandler(string runtimeDir) : CefBrowserProcessHandler
    {
        // AC-2038: native CEF children cannot see managed environment changes on Linux.
        protected override void OnBeforeChildProcessLaunch(CefCommandLine commandLine) =>
            commandLine.AppendSwitch("cockpit-cef-runtime-dir", runtimeDir);
    }
}
