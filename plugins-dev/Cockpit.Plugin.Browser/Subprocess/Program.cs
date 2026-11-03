using System.Runtime.InteropServices;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Subprocess;

// CEF's renderer, GPU and utility processes. libcef lives in the runtime directory the browser process hands on
// through the child command line, outside the plugin folder. The 8 MiB stack V8 needs comes from a thread, not editbin.
internal static class Program
{
    private const string RuntimeDirArgument = "--cockpit-cef-runtime-dir=";

    [STAThread]
    private static int Main(string[] args)
    {
        var runtimeDir = args.FirstOrDefault(argument => argument.StartsWith(RuntimeDirArgument, StringComparison.Ordinal))?[RuntimeDirArgument.Length..];
        if (string.IsNullOrEmpty(runtimeDir))
        {
            return 1;
        }

        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
            name == "libcef" ? NativeLibrary.Load(Path.Combine(runtimeDir, _LibraryName())) : nint.Zero);

        var exitCode = 0;
        var thread = new Thread(() =>
        {
            // CefGlue refuses a path off Windows, and CEF reads argv[0] as the program name, which .NET's args drop.
            // The resolver above already points at libcef.so (AC-1860).
            if (OperatingSystem.IsLinux())
            {
                CefRuntime.Load();
                exitCode = CefRuntime.ExecuteProcess(new CefMainArgs(Environment.GetCommandLineArgs()), new SubprocessApp(), nint.Zero);
            }
            else
            {
                CefRuntime.Load(runtimeDir);
                exitCode = CefRuntime.ExecuteProcess(new CefMainArgs(args), new SubprocessApp(), nint.Zero);
            }
        }, 8 * 1024 * 1024);
        thread.Start();
        thread.Join();

        return exitCode;
    }

    private static string _LibraryName() => OperatingSystem.IsWindows() ? "libcef.dll" : "libcef.so";

    private sealed class SubprocessApp : CefApp;
}
