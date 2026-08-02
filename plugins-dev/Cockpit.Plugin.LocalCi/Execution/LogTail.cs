namespace Cockpit.Plugin.LocalCi.Execution;

// Keeps the end of a run's output and throws the rest away as it arrives. A build log is mostly restore output;
// what tells you why a job failed is the last stretch of it. Bounded on both counts because either one alone
// still lets a log through that nobody wants in an agent's context: a thousand short lines, or one enormous one.
// Synchronised because a run's two output streams arrive on their own threads: a `System.Diagnostics.Process`
// services stdout and stderr as independent read loops with no ordering between them, and act writes its progress to
// stderr while the job's own output goes to stdout — so both are writing here at once for the whole of a normal run.
internal sealed class LogTail(int maxLines, int maxCharacters)
{
    // Enough to carry a failing test's name, its assertion and the summary line under it.
    public static LogTail ForFailure() => new(maxLines: 120, maxCharacters: 8000);

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private int _characters;

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            _characters += line.Length + 1;

            while (_lines.Count > maxLines || (_characters > maxCharacters && _lines.Count > 1))
            {
                _characters -= _lines.Dequeue().Length + 1;
            }
        }
    }

    public string Text() => string.Join(Environment.NewLine, Lines());

    // The kept lines, oldest first, as a snapshot. Copied under the lock rather than handed out live: a run's two
    // output streams are still writing while a caller reads this (AC-617's failure classification does, the
    // moment the run ends), and enumerating the queue itself would race them.
    public IReadOnlyList<string> Lines()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
