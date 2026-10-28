using System.Runtime.InteropServices;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Subprocess;

// CEF's renderer, GPU and utility processes. libcef lives in the runtime directory the browser process hands on
// through the environment, outside the plugin folder. The 8 MiB stack V8 needs comes from a thread, not editbin.
internal static class Program
{
    public const string RuntimeDirVariable = "COCKPIT_CEF_RUNTIME_DIR";

    [STAThread]
    private static int Main(string[] args)
    {
        var runtimeDir = Environment.GetEnvironmentVariable(RuntimeDirVariable);
        if (string.IsNullOrEmpty(runtimeDir))
        {
            return 1;
        }

        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
            name == "libcef" ? NativeLibrary.Load(Path.Combine(runtimeDir, _LibraryName())) : nint.Zero);

        var exitCode = 0;
        var thread = new Thread(() =>
        {
            // Off Windows CefGlue refuses a path, and CEF reads argv[0] as the program name, which .NET's args drop.
            // The resolver above already points at libcef.so (AC-1860).
            if (OperatingSystem.IsLinux())
            {
                CefRuntime.Load();
            }
            else
            {
                CefRuntime.Load(runtimeDir);
            }
            exitCode = CefRuntime.ExecuteProcess(new CefMainArgs(Environment.GetCommandLineArgs()), new SubprocessApp(), nint.Zero);
        }, 8 * 1024 * 1024);
        thread.Start();
        thread.Join();

        return exitCode;
    }

    private static string _LibraryName() => OperatingSystem.IsWindows() ? "libcef.dll" : "libcef.so";

    private sealed class SubprocessApp : CefApp;
}
