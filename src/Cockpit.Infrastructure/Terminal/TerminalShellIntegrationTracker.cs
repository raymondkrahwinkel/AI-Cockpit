using System.Text;

namespace Cockpit.Infrastructure.Terminal;

// Reads the shell-integration marks a shell emits into its own output (OSC 133, the FinalTerm/iTerm2 convention, and
// VS Code's OSC 633 variant of it) so `run_in_terminal` can know when a command actually finished and what it
// exited with — instead of guessing from a quiet stream or polluting the operator's visible session with a sentinel
// `echo`. The marks are invisible escape sequences, so the person watching the pane sees nothing extra, and they
// travel over SSH because the remote shell is what emits them.
//
// Only the two marks that answer a question are acted on: `B` (the shell is at a prompt waiting for input) and
// `D` (the command finished, optionally with its exit code). `C` — the command started running — is what
// takes the shell off the prompt again. That single `AtPrompt` bit is also what keeps a command from
// being typed into a full-screen program: `vim` and `htop` run *during* a command, so the shell is
// never at a prompt while one is open.
//
// *These marks are not proof.* Nothing distinguishes the shell emitting them from any program that writes the
// same bytes — a `cat` of a crafted file, a nested session. That is true of every terminal that reads OSC 133,
// and it is why what depends on them here is a *safety* check, not a security boundary: an agent that can be
// fooled into believing the shell is idle can type a command line into whatever is actually open — but it already
// holds the operator's approval to type there, and `send_terminal` does that with no prompt-state check at all,
// by design. So the spoof costs a courtesy, not a permission. The operator watching the pane is the backstop, as it
// is for everything else this MCP does.
internal sealed class TerminalShellIntegrationTracker
{
    private const char Escape = (char)0x1b;
    private const char Bell = (char)0x07;

    // An unterminated escape sequence longer than this is not one — drop the pending text rather than grow forever on binary output.
    private const int MaxPendingLength = 512;

    // The rest of this sequence has not arrived yet — hold on to it.
    private const int Incomplete = -1;

    // This cannot be a sequence; carry on from where the next one starts rather than waiting on it.
    private const int Abandoned = -2;

    private readonly StringBuilder _pending = new();

    // Whether this shell emits integration marks at all. Until one arrives there is no way to tell a finished command from a slow one.
    public bool ShellIntegrationSeen { get; private set; }

    // Whether the shell is sitting at a prompt waiting for input — so it is idle, and nothing full-screen is open.
    public bool AtPrompt { get; private set; }

    // How many commands have reported themselves started. Paired with `CommandsFinished` it tells a caller that the finish it is looking at belongs to a command that began after it sent, not to one already in flight.
    public int CommandsStarted { get; private set; }

    // How many commands have reported themselves finished. A caller snapshots this before sending and waits for it to move.
    public int CommandsFinished { get; private set; }

    // The exit code of the last finished command, or null when the shell reported none.
    public int? LastExitCode { get; private set; }

    // Feeds the pane's raw output. Sequences split across two writes are rejoined, so a mark never goes unseen because the pty flushed mid-escape.
    public void Feed(string text)
    {
        _pending.Append(text);
        var buffer = _pending.ToString();
        var keepFrom = buffer.Length;

        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != Escape)
            {
                continue;
            }

            var end = _EndOfSequence(buffer, index, out var resyncAt);
            if (end == Incomplete)
            {
                keepFrom = index; // Wait for the rest of it rather than deciding on half a sequence.
                break;
            }

            if (end == Abandoned)
            {
                // A sequence that never terminated, cut short by the start of the next one. Pick up there instead of
                // waiting forever: one stray escape — a binary file catted, a nested session — must not swallow every
                // mark that comes after it.
                index = resyncAt - 1;
                continue;
            }

            _Apply(buffer.AsSpan(index, end - index + 1));
            index = end;
        }

        _pending.Remove(0, keepFrom);
        if (_pending.Length > MaxPendingLength)
        {
            _pending.Clear();
        }
    }

    // The index of the last character of the sequence starting at `start`; `Incomplete`
    // when the rest of it has not arrived yet, or `Abandoned` when it cannot be one — then
    // `resyncAt` says where the next sequence begins.
    private static int _EndOfSequence(string buffer, int start, out int resyncAt)
    {
        resyncAt = start;
        if (start + 1 >= buffer.Length)
        {
            return Incomplete;
        }

        // OSC: ESC ] … terminated by BEL or by ST (ESC \).
        if (buffer[start + 1] == ']')
        {
            for (var index = start + 2; index < buffer.Length; index++)
            {
                if (buffer[index] == Bell)
                {
                    return index;
                }

                if (buffer[index] != Escape)
                {
                    continue;
                }

                if (index + 1 >= buffer.Length)
                {
                    return Incomplete; // Could still become an ST once the next write lands.
                }

                if (buffer[index + 1] == '\\')
                {
                    return index + 1;
                }

                resyncAt = index;
                return Abandoned;
            }

            return Incomplete;
        }

        // CSI: ESC [ … up to the final byte. Not acted on, but has to be consumed so its payload is not
        // mistaken for the start of something else.
        if (buffer[start + 1] == '[')
        {
            for (var index = start + 2; index < buffer.Length; index++)
            {
                if (buffer[index] is >= '@' and <= '~')
                {
                    return index;
                }
            }

            return Incomplete;
        }

        return start + 1; // Two-character escape.
    }

    private void _Apply(ReadOnlySpan<char> sequence)
    {
        // sequence is ESC ] <payload> <terminator>. Both conventions carry the same letters after the id.
        if (sequence.Length < 3 || sequence[1] != ']')
        {
            return;
        }

        // The terminator is one character (BEL) or two (ST, which is ESC followed by a backslash) — trim whichever
        // it is, or the exit code comes back with an escape stuck to it and fails to parse.
        var payload = sequence[^1] == '\\' && sequence.Length >= 4 && sequence[^2] == Escape
            ? sequence[2..^2]
            : sequence[2..^1];
        if (payload.StartsWith("133;"))
        {
            payload = payload[4..];
        }
        else if (payload.StartsWith("633;"))
        {
            payload = payload[4..];
        }
        else
        {
            return;
        }

        if (payload.Length == 0)
        {
            return;
        }

        ShellIntegrationSeen = true;
        switch (payload[0])
        {
            case 'B':
                AtPrompt = true;
                break;

            case 'C':
                AtPrompt = false;
                CommandsStarted++;
                break;

            case 'D':
                AtPrompt = false;
                CommandsFinished++;
                // "D" alone means finished without a reported code; "D;<n>" carries one.
                LastExitCode = payload.Length > 2 && payload[1] == ';' && int.TryParse(payload[2..], out var code)
                    ? code
                    : null;
                break;
        }
    }
}
