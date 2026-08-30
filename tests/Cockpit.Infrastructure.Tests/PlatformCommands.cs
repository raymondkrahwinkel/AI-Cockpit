namespace Cockpit.Infrastructure.Tests;

// The two child-process shapes the command-runner tests need — one that outlives a timeout, one that writes past a
// pipe's ~64 KiB buffer — spelled for the host OS. Windows has no sh/sleep/echo on PATH, and neither shape is what
// those tests are about, so they get a platform-native equivalent rather than a skip.
internal static class PlatformCommands
{
    public static (string Command, string[] Arguments) RunsForThirtySeconds() =>
        OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", "Start-Sleep 30"])
            : ("sleep", ["30"]);

    // Exactly that many characters on stdout, no trailing newline: one test asserts the length exactly.
    public static (string Command, string[] Arguments) WritesToStandardOutput(int characters) =>
        OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", $"[Console]::Out.Write('x' * {characters})"])
            : ("sh", ["-c", $"yes x | head -c {characters}"]);
}

// A test whose subject is POSIX-only behaviour. The reason must say where the other platform is covered instead.
internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute(string windowsCoverage) => Skip = OperatingSystem.IsWindows() ? windowsCoverage : null;
}
