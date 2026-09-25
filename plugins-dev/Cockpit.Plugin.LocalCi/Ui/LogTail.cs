namespace Cockpit.Plugin.LocalCi.UI;

// Keeps the end of a run's log on screen and throws the rest away as it arrives, so the log view does not grow
// without bound while a job runs. Mirrors Execution/LogTail's bounding logic on the backend side (kept separate,
// not shared, since Execution/ stays backend-only and the two are never the same instance — only individual lines,
// published one at a time over the channel, cross the boundary).
internal sealed class LogTail(int maxLines, int maxCharacters)
{
    private readonly Queue<string> _lines = new();
    private int _characters;

    public void Add(string line)
    {
        _lines.Enqueue(line);
        _characters += line.Length + 1;

        while (_lines.Count > maxLines || (_characters > maxCharacters && _lines.Count > 1))
        {
            _characters -= _lines.Dequeue().Length + 1;
        }
    }

    public string Text() => string.Join(Environment.NewLine, _lines);
}
