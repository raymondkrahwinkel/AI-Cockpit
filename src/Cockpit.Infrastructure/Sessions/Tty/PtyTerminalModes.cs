namespace Cockpit.Infrastructure.Sessions.Tty;

// Gives a freshly spawned Linux pty the line disciplines a terminal is expected to have (AC-129).
//
// Porta.Pty's `forkpty()` shim hands back a pty with every discipline flag cleared — measured against the
// real library: `stty -a` inside it reports `-opost -onlcr -icrnl -ixon -isig -icanon -echo`. The one
// that shows is `ONLCR`: without it a program's `\n` arrives as a bare line feed, and a terminal must
// then move down a row while holding the column — so `ls`, `git status` and anything else that does not write its
// own carriage return comes out as a staircase, each line starting where the previous one ended. Interactive
// shells hide it because zsh and bash configure the terminal for themselves on startup; a plain command does not.
// The rest matter just as much once you look: with `ISIG` off, Ctrl-C reaches a bare `cat` as a byte
// instead of a signal, and with `ICANON`/`ECHO` off a script's `read` sits in raw mode.
//
// *The child sets them, not us.* The obvious fix — `tcsetattr` on the master right after the spawn —
// loses a race that is easy to miss: the child is already running by then, and a short command can write its whole
// output before the call lands. That is not theoretical, it is how this was caught: the test below passed on its
// own and failed inside the full suite, where the warm process wins. Wrapping the launch in a shell that fixes the
// modes and then `exec`s the real program removes the window entirely — the disciplines are right before the
// program exists, and `exec` means no extra process survives, so the pid, signals and process tree are
// unchanged.
//
// Linux only, deliberately. Windows never reaches this code — ConPTY translates line endings itself, which is why
// the symptom is Linux-only — and macOS is left alone until it can be run on the hardware.
internal static class PtyTerminalModes
{
    // "sane" is POSIX's own name for the cooked defaults: opost/onlcr, icrnl/ixon, isig/icanon/echo, and the
    // standard control characters. Deliberately not a hand-rolled flag list — the numeric values differ per
    // platform, and stty already carries the definition every other terminal ends up with.
    private const string SaneModesThenExec = "stty sane 2>/dev/null; exec \"$0\" \"$@\"";

    // Returns the executable and argv to spawn so the pty starts with sane line disciplines. On anything other
    // than Linux this hands back exactly what it was given, so no other platform changes behaviour.
    // The shell form is `sh -c SCRIPT NAME ARGS…`, where `NAME` becomes `$0` and the rest
    // `"$@"` — so an executable path or an argument containing spaces survives without any quoting of ours.
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
