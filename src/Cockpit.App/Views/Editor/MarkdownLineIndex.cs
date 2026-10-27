using AvaloniaEdit.Document;
using Cockpit.Core.Markdown;

namespace Cockpit.App.Views.Editor;

// Line kinds on demand: a line is classified when it is drawn, from the cached state of the line above (AC-1753, R6).
internal sealed class MarkdownLineIndex
{
    // ponytail: front matter longer than this is not looked for, a scan of the whole file per key is not worth it.
    private const int FrontMatterScanLimit = 200;

    private readonly TextDocument _document;
    private readonly List<MarkdownLine> _lines = [];
    private readonly List<MarkdownLineState> _after = [];
    private int _frontMatterEnd = -1;

    public MarkdownLineIndex(TextDocument document)
    {
        _document = document;
        _document.Changed += (_, e) => Invalidate(_document.GetLineByOffset(e.Offset).LineNumber);
    }

    // Last line of the front matter, or 0 when the file has none.
    public int FrontMatterEnd
    {
        get
        {
            if (_frontMatterEnd < 0)
            {
                _frontMatterEnd = _FindFrontMatterEnd();
            }

            return _frontMatterEnd;
        }
    }

    public MarkdownLine Get(int lineNumber)
    {
        while (_lines.Count < lineNumber)
        {
            var number = _lines.Count + 1;
            var state = number == 1 ? MarkdownLineState.AtStart(FrontMatterEnd > 0) : _after[number - 2];
            _lines.Add(MarkdownLineClassifier.Classify(_document.GetText(_document.GetLineByNumber(number)), state, out var next));
            _after.Add(next);
        }

        return _lines[lineNumber - 1];
    }

    public bool IsTable(int lineNumber) =>
        lineNumber >= 1 && lineNumber <= _document.LineCount && Get(lineNumber).Kind is MarkdownLineKind.TableRow or MarkdownLineKind.TableDelimiter;

    // The run of table lines around lineNumber; a table is drawn as one block, so its edges matter.
    public (int First, int Last) TableAround(int lineNumber)
    {
        var first = lineNumber;
        while (IsTable(first - 1))
        {
            first--;
        }

        var last = lineNumber;
        while (IsTable(last + 1))
        {
            last++;
        }

        return (first, last);
    }

    private void Invalidate(int lineNumber)
    {
        var frontMatterEnd = _FindFrontMatterEnd();
        var from = frontMatterEnd != _frontMatterEnd ? 1 : lineNumber;
        _frontMatterEnd = frontMatterEnd;
        if (from - 1 < _lines.Count)
        {
            _lines.RemoveRange(from - 1, _lines.Count - from + 1);
            _after.RemoveRange(from - 1, _after.Count - from + 1);
        }
    }

    private int _FindFrontMatterEnd()
    {
        if (!MarkdownLineClassifier.OpensFrontMatter(_document.GetText(_document.GetLineByNumber(1))))
        {
            return 0;
        }

        var last = Math.Min(_document.LineCount, FrontMatterScanLimit);
        for (var number = 2; number <= last; number++)
        {
            if (MarkdownLineClassifier.ClosesFrontMatter(_document.GetText(_document.GetLineByNumber(number))))
            {
                return number;
            }
        }

        return 0;
    }
}
