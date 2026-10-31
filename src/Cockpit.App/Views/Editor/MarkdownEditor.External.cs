using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.App.Views.Editor;

public sealed partial class MarkdownEditor
{
    private readonly List<ExternalLine> _externalLines = [];
    private readonly DispatcherTimer _externalClock = new() { Interval = TimeSpan.FromSeconds(1) };

    internal IReadOnlyList<int> ExternalMarkedLines => [.. _externalLines.Where(mark => !mark.Start.IsDeleted && !mark.End.IsDeleted).Select(mark => mark.Start.Line)];

    internal void MarkExternalEdit(ExternalTextEdit edit)
    {
        if (DateTimeOffset.UtcNow - edit.At >= TimeSpan.FromMinutes(10))
        {
            return;
        }
        var document = _editor.Document;
        foreach (var range in edit.Ranges)
        {
            if (range.Start < 0 || range.End <= range.Start || range.End > document.TextLength)
            {
                continue;
            }
            var first = document.GetLineByOffset(range.Start).LineNumber;
            var last = document.GetLineByOffset(range.End - 1).LineNumber;
            for (var number = first; number <= last; number++)
            {
                var line = document.GetLineByNumber(number);
                _externalLines.RemoveAll(mark => !mark.Start.IsDeleted && mark.Start.Line == number);
                _externalLines.Add(new ExternalLine(document.CreateAnchor(line.Offset), document.CreateAnchor(line.EndOffset), edit.Who, edit.At));
            }
        }
        _externalClock.Start();
        _Redraw();
    }

    private void _ExpireExternalLines()
    {
        _externalLines.RemoveAll(mark => mark.Start.IsDeleted || mark.End.IsDeleted || DateTimeOffset.UtcNow - mark.At >= TimeSpan.FromMinutes(10));
        if (_externalLines.Count == 0)
        {
            _externalClock.Stop();
        }
        else
        {
            _externalClock.Start();
        }
        _Redraw();
    }

    private void _ExternalChanging(DocumentChangeEventArgs change)
    {
        _externalLines.RemoveAll(mark => mark.Start.IsDeleted || mark.End.IsDeleted
            || change.Offset <= mark.End.Offset && change.Offset + change.RemovalLength >= mark.Start.Offset);
    }

    private void _PaintExternal(DrawingContext context, int number, Rect row, bool gutter)
    {
        var mark = _externalLines.FirstOrDefault(item => !item.Start.IsDeleted && !item.End.IsDeleted && item.Start.Line == number);
        if (mark is null || DateTimeOffset.UtcNow - mark.At >= TimeSpan.FromMinutes(10))
        {
            return;
        }
        context.FillRectangle(ThemeBrush.Resolve(gutter ? "CockpitStatusBackgroundBrush" : "CockpitCategoryTintPurpleBrush", "Transparent"), gutter ? row.WithWidth(2) : row);
        if (!gutter && !_externalLines.Any(item => !item.Start.IsDeleted && item.Who == mark.Who && item.At == mark.At && item.Start.Line < number))
        {
            var age = DateTimeOffset.UtcNow - mark.At;
            var label = age.TotalMinutes < 1 ? "just now" : $"{(int)age.TotalMinutes} min ago";
            var who = new FormattedText($"{mark.Who} · {label}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, weight: FontWeight.Medium), 10.5, TextFaint);
            context.DrawText(who, new Point(row.Right - who.Width - 12, row.Top + 2));
        }
    }

    private sealed record ExternalLine(TextAnchor Start, TextAnchor End, string Who, DateTimeOffset At);
}
