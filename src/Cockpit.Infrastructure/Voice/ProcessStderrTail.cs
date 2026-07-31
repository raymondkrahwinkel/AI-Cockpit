namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// A bounded tail of a child process's stderr (AC-534): the last <see cref="MaxLines"/> lines, capped at
/// <see cref="MaxChars"/> characters in total, so a chatty native runtime (whisper.cpp, CUDA) can never grow this
/// unbounded. A line longer than the whole budget is cut to fit rather than dropped.
/// Feed it from a <c>Process.ErrorDataReceived</c> handler registered after <c>BeginErrorReadLine</c> — that
/// combination is what keeps the OS pipe buffer (~64 KB on Linux) draining continuously, since an unread
/// redirected stream blocks the writing child once its buffer fills. This type only remembers; it never logs,
/// so routine stderr chatter never floods the log the way the idle-reaper noise did (AC-533) — a caller reads
/// <see cref="Snapshot"/> only once there is an actual failure to explain.
/// </summary>
internal sealed class ProcessStderrTail
{
    internal const int MaxLines = 20;

    /// <summary>
    /// The cap is counted in characters, not bytes: a non-ASCII line therefore occupies more room on disk than
    /// this number suggests. That is deliberate — the point is a bound, and counting UTF-8 bytes per line would
    /// cost an encode on a path that runs for every line a chatty runtime writes.
    /// </summary>
    internal const int MaxChars = 4096;

    /// <summary>Marks a line that was longer than the whole budget and had to be cut to fit.</summary>
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

        // A single line can be longer than the entire budget — a native runtime that dumps a whole state on one
        // line is exactly the case this type exists for. Cut it to fit instead of letting the eviction loop below
        // drop it: evicting to get under the cap would throw away the only line that had the answer in it, and the
        // lines before it as well, leaving an empty tail at the one moment it was supposed to explain something.
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

    /// <summary>The remembered lines, oldest first, newline-joined — or empty if the process said nothing (yet).</summary>
    public string Snapshot()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
