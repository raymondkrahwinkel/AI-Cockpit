using System.Runtime.InteropServices;

namespace Cockpit.Core.Diagnostics;

/// <summary>
/// What this install is running on (AC-58): the operating system, the CPU architecture the OS and this process
/// were built for, and the runtime and toolkit versions. The macOS blind spot that started AC-58 was as much about
/// not knowing the tester's arch (Apple Silicon vs Intel) as about memory — so the panel says it plainly.
/// <para>
/// The Avalonia and app versions come from the App layer, which is the only one that references the toolkit; this
/// type reads the rest from <see cref="RuntimeInformation"/>, which needs no platform code.
/// </para>
/// </summary>
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
