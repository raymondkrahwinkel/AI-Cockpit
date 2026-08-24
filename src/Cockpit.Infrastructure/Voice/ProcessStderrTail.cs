namespace Cockpit.Infrastructure.Voice;

// AC-534: Bounded tail of a child process's stderr (last MaxLines, capped MaxChars) so a chatty native
// runtime (whisper.cpp, CUDA) can't grow this unbounded. Feed it from ErrorDataReceived after
// BeginErrorReadLine to keep draining the OS pipe. Only remembers, never logs (AC-533) — read Snapshot on failure.
internal sealed class ProcessStderrTail
{
    internal const int MaxLines = 20;

    // The cap is counted in characters, not bytes: a non-ASCII line therefore occupies more room on disk than
    // this number suggests. That is deliberate — the point is a bound, and counting UTF-8 bytes per line would
    // cost an encode on a path that runs for every line a chatty runtime writes.
    internal const int MaxChars = 4096;

    // Marks a line that was longer than the whole budget and had to be cut to fit.
    internal const string TruncationMarker = "… (truncated)";

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private int _chars;

    public void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        // A single line can exceed the whole budget (a native runtime dumping full state on one line) — cut it
        // to fit rather than let the eviction loop drop it, which would throw away the one line with the
        // answer and the lines before it, leaving an empty tail exactly when it needed to explain something.
        if (line.Length > MaxChars)
        {
            line = string.Concat(line.AsSpan(0, MaxChars - TruncationMarker.Length), TruncationMarker);
        }

        lock (_gate)
        {
            _lines.Enqueue(line);
            _chars += line.Length;

            // Keeps at least the newest line, whatever its size — the condition above already made it fit.
            while (_lines.Count > MaxLines || (_chars > MaxChars && _lines.Count > 1))
            {
                _chars -= _lines.Dequeue().Length;
            }
        }
    }

    // The remembered lines, oldest first, newline-joined — or empty if the process said nothing (yet).
    public string Snapshot()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
