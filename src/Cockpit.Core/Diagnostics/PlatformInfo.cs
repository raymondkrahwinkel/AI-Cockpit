using System.Runtime.InteropServices;

namespace Cockpit.Core.Diagnostics;

// AC-1013 (AC-58): What this install runs on — OS, CPU arch, runtime/toolkit versions — since AC-58's macOS
// blind spot was as much about not knowing the tester's arch (Apple Silicon vs Intel) as about memory. Avalonia
// and app versions come from the App layer (the only one referencing the toolkit); the rest from `RuntimeInformation`.
public sealed record PlatformInfo(
    string OperatingSystem,
    Architecture OsArchitecture,
    Architecture ProcessArchitecture,
    string RuntimeVersion,
    string AvaloniaVersion,
    string AppVersion)
{
    public static PlatformInfo Current(string avaloniaVersion, string appVersion) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture,
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.FrameworkDescription,
        avaloniaVersion,
        appVersion);
}
