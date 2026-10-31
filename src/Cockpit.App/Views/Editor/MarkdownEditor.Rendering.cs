using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Cockpit.App.Theming;
using Cockpit.Core.Markdown;

namespace Cockpit.App.Views.Editor;

public sealed partial class MarkdownEditor
{
    private static IBrush TextPrimary => ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef");
    private static IBrush TextSecondary => ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5");
    private static IBrush TextFaint => ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78");
    private static IBrush Hairline => ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39");
    private static IBrush Panel => ThemeBrush.Resolve("CockpitPanelBgBrush", "#1a1d24");
    private static IBrush Inset => ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430");
    private static IBrush Accent => ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb");
    private static IBrush Link => ThemeBrush.Resolve("CockpitClickablePathFgBrush", "#93b4f7");
    private static IBrush CaretLineFill => ThemeBrush.Resolve("CockpitAccentSelectionBrush", "#292563eb");
    private static IBrush CompareTheirsFill => ThemeBrush.Resolve("CockpitStatusErrorChipTintBrush", "#26d64545");
    private static IBrush CompareMineFill => ThemeBrush.Resolve("CockpitStatusDoneChipTintBrush", "#265aa576");
    private static IBrush CompareTheirsBar => ThemeBrush.Resolve("CockpitStatusErrorBrush", "#d64545");
    private static IBrush CompareMineBar => ThemeBrush.Resolve("CockpitStatusDoneBrush", "#5aa576");
    private static IBrush CaretLineEdge => ThemeBrush.Resolve("CockpitAccentRingTintBrush", "#402563eb");
    private static IBrush LocalLineEdge => ThemeBrush.Resolve("CockpitStatusWaitingBrush", "#d99a37");
    private readonly HashSet<int> _localLines = [];

    internal void SetLocalLines(IEnumerable<int> lines)
    {
        var next = lines.ToHashSet();
        if (_localLines.SetEquals(next))
        {
            return;
        }
        _localLines.Clear();
        _localLines.UnionWith(next);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var margin in _editor.TextArea.LeftMargins)
        {
            margin.InvalidateVisual();
        }
    }

    // A table and front matter show their Markdown while the caret is anywhere inside them, not just on its line.
    internal bool IsRaw(int lineNumber, MarkdownLine line)
    {
        var caret = CaretLine;
        return caret > 0 && (caret == lineNumber || line.Kind switch
        {
            MarkdownLineKind.FrontMatter => caret <= FrontMatterEnd,
            MarkdownLineKind.TableRow or MarkdownLineKind.TableDelimiter => IsTable(caret) && TableAround(caret) == TableAround(lineNumber),
            _ => false,
        });
    }

    // The label beside the first line of a block the editor has no rendering for; null on every other line.
    internal string? KeptAsTextLabel(int lineNumber)
    {
        var kind = Classify(lineNumber);
        if (!kind.IsKeptAsText || (lineNumber > 1 && Classify(lineNumber - 1).Kind == kind.Kind))
        {
            return null;
        }

        return kind.Kind switch
        {
            MarkdownLineKind.Html => "HTML — kept as text",
            MarkdownLineKind.Footnote => "footnote — kept as text",
            _ => "link definition — kept as text",
        };
    }

    private static bool HasInlines(MarkdownLineKind kind) => kind is MarkdownLineKind.Paragraph or MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2
        or MarkdownLineKind.Heading3 or MarkdownLineKind.Bullet or MarkdownLineKind.Task or MarkdownLineKind.Numbered or MarkdownLineKind.Quote;

    private static Typeface With(Typeface typeface, FontFamily? family = null, FontStyle? style = null, FontWeight? weight = null) =>
        new(family ?? typeface.FontFamily, style ?? typeface.Style, weight ?? typeface.Weight);

    // Fonts and colours per line kind and per inline span; the caret line keeps its look but shows its signs, faint.
    private sealed class Colorizer(MarkdownEditor owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner.ShowSource || line.Length == 0)
            {
                return;
            }

            var number = line.LineNumber;
            var kind = owner.Classify(number);
            var raw = owner.IsRaw(number, kind);
            switch (kind.Kind)
            {
                case MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3:
                    var (size, weight) = kind.Kind switch
                    {
                        MarkdownLineKind.Heading1 => (24d, FontWeight.Bold),
                        MarkdownLineKind.Heading2 => (18d, FontWeight.SemiBold),
                        _ => (15.5, FontWeight.SemiBold),
                    };
                    _Style(line.Offset, line.EndOffset, e =>
                    {
                        e.TextRunProperties.SetFontRenderingEmSize(size);
                        e.TextRunProperties.SetTypeface(With(e.TextRunProperties.Typeface, weight: weight));
                    });
                    break;
                case MarkdownLineKind.Quote:
                    _Style(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(TextSecondary));
                    break;
                case MarkdownLineKind.Task when kind.Done:
                    _Style(line.Offset + kind.MarkerLength, line.EndOffset, e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(TextFaint);
                        e.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                    });
                    break;
                case MarkdownLineKind.Code or MarkdownLineKind.TableRow or MarkdownLineKind.TableDelimiter:
                    _Mono(line, 12.5, TextPrimary);
                    break;
                case MarkdownLineKind.Fence or MarkdownLineKind.Rule:
                    _Mono(line, 11, TextFaint);
                    break;
                case MarkdownLineKind.Html or MarkdownLineKind.Footnote or MarkdownLineKind.LinkDefinition or MarkdownLineKind.FrontMatter:
                    _Mono(line, 12, TextSecondary);
                    break;
            }

            if (kind.MarkerLength > 0)
            {
                _Style(line.Offset, line.Offset + kind.MarkerLength, e => e.TextRunProperties.SetForegroundBrush(raw ? TextFaint : TextSecondary));
            }

            if (HasInlines(kind.Kind))
            {
                _Inlines(line, kind, raw);
            }
        }

        private void _Inlines(DocumentLine line, MarkdownLine kind, bool raw)
        {
            var start = line.Offset + kind.MarkerLength;
            foreach (var span in MarkdownLineClassifier.Inlines(CurrentContext.Document.GetText(start, line.EndOffset - start)))
            {
                Action<VisualLineElement> style = span.Kind switch
                {
                    MarkdownInlineKind.Bold => e => e.TextRunProperties.SetTypeface(With(e.TextRunProperties.Typeface, weight: FontWeight.Bold)),
                    MarkdownInlineKind.Italic => e => e.TextRunProperties.SetTypeface(With(e.TextRunProperties.Typeface, style: FontStyle.Italic)),
                    MarkdownInlineKind.Code => e =>
                    {
                        e.TextRunProperties.SetTypeface(With(e.TextRunProperties.Typeface, family: MonoFont));
                        e.TextRunProperties.SetFontRenderingEmSize(e.TextRunProperties.FontRenderingEmSize * 0.86);
                        e.TextRunProperties.SetBackgroundBrush(Inset);
                    },
                    _ => e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(Link);
                        e.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    },
                };
                _Style(start + span.Start, start + span.End, style);
                if (raw)
                {
                    _Style(start + span.Start, start + span.Start + span.OpenLength, e => e.TextRunProperties.SetForegroundBrush(TextFaint));
                    _Style(start + span.End - span.CloseLength, start + span.End, e => e.TextRunProperties.SetForegroundBrush(TextFaint));
                }
            }
        }

        private void _Mono(DocumentLine line, double size, IBrush brush) => _Style(line.Offset, line.EndOffset, e =>
        {
            e.TextRunProperties.SetTypeface(With(e.TextRunProperties.Typeface, family: MonoFont));
            e.TextRunProperties.SetFontRenderingEmSize(size);
            e.TextRunProperties.SetForegroundBrush(brush);
        });

        private void _Style(int start, int end, Action<VisualLineElement> action)
        {
            if (end > start)
            {
                ChangeLinePart(start, end, action);
            }
        }
    }

    // What replaces Markdown signs off the caret line: nothing for a sign, a glyph for a list, a drawn table or strip for a block.
    private sealed class Elements(MarkdownEditor owner) : VisualLineElementGenerator
    {
        private readonly List<(int Offset, int Length, Func<VisualLineElement> Build)> _pending = [];

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _pending.Clear();
            var line = context.VisualLine.FirstDocumentLine;
            var kind = owner.ShowSource ? default : owner.Classify(line.LineNumber);
            if (owner.ShowSource || owner.IsRaw(line.LineNumber, kind) || !_Block(line, kind))
            {
                owner.Collapser.ReleaseAt(line);
            }

            if (!owner.ShowSource && !owner.IsRaw(line.LineNumber, kind) && line.Length > 0)
            {
                _Line(line, kind);
            }

            _pending.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var element in _pending)
            {
                if (element.Offset >= startOffset)
                {
                    return element.Offset;
                }
            }

            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            foreach (var element in _pending)
            {
                if (element.Offset == offset)
                {
                    return element.Build();
                }
            }

            return null;
        }

        // Front matter and a table are drawn as one element over all their lines; the lines under it are collapsed.
        private bool _Block(DocumentLine line, MarkdownLine kind)
        {
            var number = line.LineNumber;
            var document = CurrentContext.Document;
            if (kind.Kind == MarkdownLineKind.FrontMatter && number == 1 && owner.FrontMatterEnd > 1)
            {
                var last = document.GetLineByNumber(owner.FrontMatterEnd);
                owner.Collapser.Collapse(line, last);
                _Add(line.Offset, last.EndOffset - line.Offset, () => _FrontMatterStrip(owner.FrontMatterEnd - 2));
                return true;
            }

            if (kind.Kind is MarkdownLineKind.TableRow or MarkdownLineKind.TableDelimiter && !owner.IsTable(number - 1))
            {
                var (_, lastNumber) = owner.TableAround(number);
                var last = document.GetLineByNumber(lastNumber);
                owner.Collapser.Collapse(line, last);
                var rows = Enumerable.Range(number, lastNumber - number + 1)
                    .Where(n => owner.Classify(n).Kind == MarkdownLineKind.TableRow)
                    .Select(n => _Cells(owner.LineText(n)))
                    .ToList();
                _Add(line.Offset, last.EndOffset - line.Offset, () => _Table(rows, number));
                return true;
            }

            return false;
        }

        private void _Line(DocumentLine line, MarkdownLine kind)
        {
            var at = line.Offset;
            switch (kind.Kind)
            {
                case MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 or MarkdownLineKind.Quote:
                    _Hide(at, kind.MarkerLength);
                    break;
                case MarkdownLineKind.Bullet:
                    _Text(at, kind.MarkerLength, new string(' ', _Indent(line)) + " •  ");
                    break;
                case MarkdownLineKind.Numbered:
                    _Text(at, kind.MarkerLength, new string(' ', _Indent(line)) + " " + kind.Number + " ");
                    break;
                case MarkdownLineKind.Task:
                    var number = line.LineNumber;
                    _Add(at, kind.MarkerLength, () => _TaskBox(kind.Done, number));
                    break;
                case MarkdownLineKind.Rule:
                    _Hide(at, line.Length);
                    break;
                case MarkdownLineKind.Fence:
                    var language = owner.LineText(line.LineNumber).Trim().TrimStart('`', '~').Trim();
                    _Text(at, line.Length, language.Length > 0 ? "code · " + language : "​");
                    break;
            }

            if (!HasInlines(kind.Kind))
            {
                return;
            }

            var start = at + kind.MarkerLength;
            foreach (var span in MarkdownLineClassifier.Inlines(owner.LineText(line.LineNumber)[kind.MarkerLength..]))
            {
                _Hide(start + span.Start, span.OpenLength);
                _Hide(start + span.End - span.CloseLength, span.CloseLength);
            }
        }

        private void _Hide(int offset, int length)
        {
            if (length > 0)
            {
                _Add(offset, length, () => new Control());
            }
        }

        private void _Add(int offset, int length, Func<Control> build) => _pending.Add((offset, length, () => new InlineObjectElement(length, build())));

        // Drawn as text on the line's own baseline, in the colours the colorizer gave the signs it replaces.
        private void _Text(int offset, int length, string text) => _pending.Add((offset, length, () => new FormattedTextElement(text, length)));

        private int _Indent(DocumentLine line) => owner.LineText(line.LineNumber).TakeWhile(char.IsWhiteSpace).Count();

        private Control _TaskBox(bool done, int lineNumber)
        {
            var box = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = done ? Material.Icons.MaterialIconKind.CheckboxMarked : Material.Icons.MaterialIconKind.CheckboxBlankOutline,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, -2),
                Foreground = done ? Accent : TextSecondary,
                Cursor = owner.IsReadOnly ? null : new Cursor(StandardCursorType.Hand),
            };
            box.PointerPressed += (_, e) =>
            {
                owner.ToggleTask(lineNumber);
                e.Handled = true;
            };
            return box;
        }

        private Control _FrontMatterStrip(int lineCount)
        {
            var inlines = new InlineCollection();
            var text = new TextBlock { FontSize = 12, Foreground = TextSecondary, Inlines = inlines };
            inlines.Add(new Run($"▸ Front matter · {lineCount} line{(lineCount == 1 ? "" : "s")} "));
            inlines.Add(new Run("— not shown in the editor, kept byte for byte") { Foreground = TextFaint });
            var strip = new Border
            {
                Child = text,
                BorderBrush = Hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 5),
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = owner.IsReadOnly ? null : new Cursor(StandardCursorType.Hand),
            };
            strip.PointerPressed += (_, e) =>
            {
                owner.EditLine(Math.Min(2, owner.FrontMatterEnd), atEnd: true);
                e.Handled = true;
            };
            return strip;
        }

        private Control _Table(List<List<string>> rows, int firstLine)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            for (var c = 0; c < columns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            }

            for (var r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < columns; c++)
                {
                    var cell = new Border
                    {
                        BorderBrush = Hairline,
                        BorderThickness = new Thickness(1, 1, c == columns - 1 ? 1 : 0, r == rows.Count - 1 ? 1 : 0),
                        Background = r == 0 ? Panel : Brushes.Transparent,
                        Padding = new Thickness(10, 5),
                        Child = _InlineText(c < rows[r].Count ? rows[r][c] : string.Empty, 13.5, r == 0),
                    };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            grid.PointerPressed += (_, e) =>
            {
                owner.EditLine(firstLine, atEnd: true);
                e.Handled = true;
            };
            return grid;
        }

        private static List<string> _Cells(string row)
        {
            var cells = new List<string>();
            var current = new System.Text.StringBuilder();
            var trimmed = row.Trim();
            for (var i = trimmed.StartsWith('|') ? 1 : 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
                {
                    current.Append('|');
                    i++;
                }
                else if (trimmed[i] == '|')
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(trimmed[i]);
                }
            }

            if (current.ToString().Trim().Length > 0)
            {
                cells.Add(current.ToString().Trim());
            }

            return cells;
        }

        private static TextBlock _InlineText(string text, double size, bool header)
        {
            var inlines = new InlineCollection();
            var block = new TextBlock { FontSize = size, Foreground = TextPrimary, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, Inlines = inlines };
            var at = 0;
            foreach (var span in MarkdownLineClassifier.Inlines(text))
            {
                if (span.Start < at)
                {
                    continue;
                }

                inlines.Add(new Run(text[at..span.Start]));
                var run = new Run(text[(span.Start + span.OpenLength)..(span.End - span.CloseLength)]);
                switch (span.Kind)
                {
                    case MarkdownInlineKind.Bold:
                        run.FontWeight = FontWeight.Bold;
                        break;
                    case MarkdownInlineKind.Italic:
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case MarkdownInlineKind.Code:
                        run.FontFamily = MonoFont;
                        run.Background = Inset;
                        break;
                    default:
                        run.Foreground = Link;
                        run.TextDecorations = TextDecorations.Underline;
                        break;
                }

                inlines.Add(run);
                at = span.End;
            }

            inlines.Add(new Run(text[at..]));
            return block;
        }
    }

    // Line surfaces under the text: code blocks, quote bars, the "kept as text" mark, the caret line and a marked line.
    private sealed class LineBackgrounds(MarkdownEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext) => Paint(owner, textView, drawingContext, textView.Bounds.Width, gutter: false);

        // Shared with the gutter, so a fill runs on under the text's left edge and the bars sit beside the text, not on it.
        internal static void Paint(MarkdownEditor owner, TextView textView, DrawingContext drawingContext, double width, bool gutter)
        {
            if (!textView.VisualLinesValid)
            {
                return;
            }

            foreach (var visual in textView.VisualLines)
            {
                var number = visual.FirstDocumentLine.LineNumber;
                var top = Math.Round(visual.VisualTop - textView.ScrollOffset.Y);
                var row = new Rect(0, top, width, Math.Round(visual.VisualTop + visual.Height - textView.ScrollOffset.Y) - top);
                _PaintCompare(owner, drawingContext, number, row, gutter);
                if (owner.ShowSource)
                {
                    owner._PaintExternal(drawingContext, number, row, gutter);
                    if (gutter && owner._localLines.Contains(number))
                    {
                        drawingContext.FillRectangle(LocalLineEdge, row.WithWidth(2));
                    }
                    continue;
                }

                var kind = owner.Classify(number);
                if (number == owner.CaretLine)
                {
                    drawingContext.FillRectangle(CaretLineFill, row);
                }
                else if (number == owner.MarkedLine)
                {
                    drawingContext.FillRectangle(Inset, row);
                }
                else if (kind.Kind is MarkdownLineKind.Code or MarkdownLineKind.Fence)
                {
                    drawingContext.FillRectangle(Panel, row);
                }

                if (gutter)
                {
                    var bar = owner._localLines.Contains(number) ? LocalLineEdge
                        : number == owner.MarkedLine ? TextFaint
                        : number == owner.CaretLine ? CaretLineEdge
                        : kind.Kind == MarkdownLineKind.Quote || kind.IsKeptAsText ? Hairline
                        : null;
                    if (bar is not null)
                    {
                        drawingContext.FillRectangle(bar, row.WithWidth(!owner._localLines.Contains(number) && kind.Kind == MarkdownLineKind.Quote ? 3 : 2));
                    }
                }
                else if (kind.Kind == MarkdownLineKind.Rule && !owner.IsRaw(number, kind))
                {
                    drawingContext.FillRectangle(Hairline, new Rect(0, Math.Round(row.Center.Y), width, 1));
                }
                else if (owner.KeptAsTextLabel(number) is { } label)
                {
                    _Badge(drawingContext, visual, row, label);
                }
                if (!gutter || !owner._localLines.Contains(number))
                {
                    owner._PaintExternal(drawingContext, number, row, gutter);
                }
            }
        }

        // Red for a line only the source has, green for a line only the editor has, with who wrote it.
        private static void _PaintCompare(MarkdownEditor owner, DrawingContext drawingContext, int number, Rect row, bool gutter)
        {
            if (owner._compare is not { } compare || number > compare.Count || compare[number - 1].Change == LineChange.Same)
            {
                return;
            }

            var theirs = compare[number - 1].Change == LineChange.OnlyTheirs;
            if (gutter)
            {
                drawingContext.FillRectangle(theirs ? CompareTheirsBar : CompareMineBar, row.WithWidth(3));
                return;
            }

            drawingContext.FillRectangle(theirs ? CompareTheirsFill : CompareMineFill, row);
            var who = new FormattedText(theirs ? owner._compareWho : "you", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, weight: FontWeight.Medium), 10.5, TextFaint);
            drawingContext.DrawText(who, new Point(row.Right - who.Width - 12, row.Top + 2));
        }

        private static void _Badge(DrawingContext drawingContext, VisualLine visual, Rect row, string label)
        {
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, weight: FontWeight.Medium), 10.5, TextSecondary);
            var lastLine = visual.TextLines[^1];
            var x = lastLine.WidthIncludingTrailingWhitespace + 10;
            var y = row.Bottom - lastLine.Height + (lastLine.Height - text.Height - 2) / 2;
            drawingContext.DrawRectangle(Inset, null, new Rect(x, y, text.Width + 14, text.Height + 2), 9, 9);
            drawingContext.DrawText(text, new Point(x + 7, y + 1));
        }
    }

    // A strip left of the text for the quote, "kept as text" and marked-line bars, and the start of a line's fill.
    private sealed class Gutter(MarkdownEditor owner) : AvaloniaEdit.Editing.AbstractMargin
    {
        internal const double Thickness = 14;

        protected override Size MeasureOverride(Size availableSize) => new(Thickness, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= _Invalidate;
            }

            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += _Invalidate;
            }
        }

        public override void Render(DrawingContext drawingContext)
        {
            if (TextView is { } textView)
            {
                LineBackgrounds.Paint(owner, textView, drawingContext, Bounds.Width, gutter: true);
            }
        }

        private void _Invalidate(object? sender, EventArgs e) => InvalidateVisual();
    }
}

// Keeps the lines under a drawn block (front matter, a table) collapsed, and lets go when the caret or an edit gets near.
internal sealed class BlockCollapser(TextView textView)
{
    private readonly Dictionary<DocumentLine, CollapsedLineSection> _sections = [];

    public void Collapse(DocumentLine first, DocumentLine last)
    {
        if (_sections.TryGetValue(first, out var section))
        {
            if (section.IsCollapsed && section.End == last)
            {
                return;
            }

            section.Uncollapse();
            _sections.Remove(first);
        }

        if (last.LineNumber > first.LineNumber)
        {
            _sections[first] = textView.CollapseLines(first.NextLine, last);
        }
    }

    public void ReleaseAt(DocumentLine first)
    {
        if (_sections.Remove(first, out var section))
        {
            section.Uncollapse();
        }
    }

    // Lets go of every block that holds lineNumber or touches it; a non-positive line lets go of nothing.
    public void Release(int lineNumber)
    {
        foreach (var (first, section) in _sections.ToList())
        {
            var gone = first.IsDeleted || !section.IsCollapsed || section.End is null;
            if (lineNumber > 0 && (gone || (section.End is { } end && lineNumber >= first.LineNumber - 1 && lineNumber <= end.LineNumber + 1)))
            {
                section.Uncollapse();
                _sections.Remove(first);
            }
        }
    }

    public void ReleaseAll()
    {
        foreach (var section in _sections.Values)
        {
            section.Uncollapse();
        }

        _sections.Clear();
    }
}
