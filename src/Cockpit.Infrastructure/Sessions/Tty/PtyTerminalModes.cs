namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Gives a freshly spawned Linux pty the line disciplines a terminal is expected to have (AC-129).
/// <para>
/// Porta.Pty's <c>forkpty()</c> shim hands back a pty with every discipline flag cleared — measured against the
/// real library: <c>stty -a</c> inside it reports <c>-opost -onlcr -icrnl -ixon -isig -icanon -echo</c>. The one
/// that shows is <c>ONLCR</c>: without it a program's <c>\n</c> arrives as a bare line feed, and a terminal must
/// then move down a row while holding the column — so `ls`, `git status` and anything else that does not write its
/// own carriage return comes out as a staircase, each line starting where the previous one ended. Interactive
/// shells hide it because zsh and bash configure the terminal for themselves on startup; a plain command does not.
/// The rest matter just as much once you look: with <c>ISIG</c> off, Ctrl-C reaches a bare <c>cat</c> as a byte
/// instead of a signal, and with <c>ICANON</c>/<c>ECHO</c> off a script's <c>read</c> sits in raw mode.
/// </para>
/// <para>
/// <b>The child sets them, not us.</b> The obvious fix — <c>tcsetattr</c> on the master right after the spawn —
/// loses a race that is easy to miss: the child is already running by then, and a short command can write its whole
/// output before the call lands. That is not theoretical, it is how this was caught: the test below passed on its
/// own and failed inside the full suite, where the warm process wins. Wrapping the launch in a shell that fixes the
/// modes and then <c>exec</c>s the real program removes the window entirely — the disciplines are right before the
/// program exists, and <c>exec</c> means no extra process survives, so the pid, signals and process tree are
/// unchanged.
/// </para>
/// <para>
/// Linux only, deliberately. Windows never reaches this code — ConPTY translates line endings itself, which is why
/// the symptom is Linux-only — and macOS is left alone until it can be run on the hardware.
/// </para>
/// </summary>
internal static class PtyTerminalModes
{
    // "sane" is POSIX's own name for the cooked defaults: opost/onlcr, icrnl/ixon, isig/icanon/echo, and the
    // standard control characters. Deliberately not a hand-rolled flag list — the numeric values differ per
    // platform, and stty already carries the definition every other terminal ends up with.
    private const string SaneModesThenExec = "stty sane 2>/dev/null; exec \"$0\" \"$@\"";

    /// <summary>
    /// Returns the executable and argv to spawn so the pty starts with sane line disciplines. On anything other
    /// than Linux this hands back exactly what it was given, so no other platform changes behaviour.
    /// </summary>
    /// <remarks>
    /// The shell form is <c>sh -c SCRIPT NAME ARGS…</c>, where <c>NAME</c> becomes <c>$0</c> and the rest
    /// <c>"$@"</c> — so an executable path or an argument containing spaces survives without any quoting of ours.
    /// </remarks>
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
