namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-1013: Gives a freshly spawned Linux pty the line disciplines a terminal is expected to have (AC-129) —
// Porta.Pty's `forkpty()` shim hands one back with every discipline flag cleared (most visibly `ONLCR`, causing a
// staircase effect). The child sets them via a wrapping shell rather than `tcsetattr` after spawn, to close a real race; Linux-only since ConPTY/macOS don't show the symptom.
internal static class PtyTerminalModes
{
    // "sane" is POSIX's own name for the cooked defaults: opost/onlcr, icrnl/ixon, isig/icanon/echo, and the
    // standard control characters. Deliberately not a hand-rolled flag list — the numeric values differ per
    // platform, and stty already carries the definition every other terminal ends up with.
    private const string SaneModesThenExec = "stty sane 2>/dev/null; exec \"$0\" \"$@\"";

    // Returns the executable and argv to spawn so the pty starts with sane line disciplines; on anything other
    // than Linux this hands back exactly what it was given. The shell form is `sh -c SCRIPT NAME ARGS…`, where
    // `NAME` becomes `$0` and `"$@"` the rest — so paths or arguments with spaces survive without our quoting.
    public static (string App, string[] CommandLine) WrapForSaneModes(
        string executablePath, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux())
        {
            return (executablePath, arguments.ToArray());
        }

        string[] commandLine = ["-c", SaneModesThenExec, executablePath, .. arguments];
        return ("/bin/sh", commandLine);
    }
}
