using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Cockpit.Core.Markdown;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Views.Editor;

// The live-preview Markdown editor (AC-1753): the text is the file, the view only draws formatting over it.
public sealed partial class MarkdownEditor : UserControl, IMarkdownEditor
{
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<MarkdownEditor, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> ShowSourceProperty = AvaloniaProperty.Register<MarkdownEditor, bool>(nameof(ShowSource));

    private const double BodySize = 14;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Noto Sans Mono, DejaVu Sans Mono, monospace");

    private readonly TextEditor _editor;
    private readonly MarkdownLineIndex _index;
    private readonly BlockCollapser _collapser;
    private readonly Border _toolbar;
    private readonly ComboBox _blockStyle;
    private readonly ToggleButton _sourceToggle;
    private int _caretLine = 1;
    private int _markedLine;
    private bool _syncing;

    public MarkdownEditor()
    {
        _editor = new TextEditor
        {
            WordWrap = true,
            ShowLineNumbers = false,
            FontSize = BodySize,
            FontFamily = FontFamily.Default,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = _ColumnPadding(0),
        };
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.EnableRectangularSelection = false;
        _editor.Bind(TextEditor.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));

        _index = new MarkdownLineIndex(_editor.Document);
        _collapser = new BlockCollapser(_editor.TextArea.TextView);
        var textView = _editor.TextArea.TextView;
        textView.BackgroundRenderers.Add(new LineBackgrounds(this));
        textView.LineTransformers.Add(new Colorizer(this));
        textView.ElementGenerators.Add(new Elements(this));
        _editor.TextArea.LeftMargins.Add(new Gutter(this));
        _editor.SizeChanged += (_, e) => _editor.Padding = _ColumnPadding(e.NewSize.Width);

        _editor.TextArea.Caret.PositionChanged += (_, _) => _OnCaretMoved();
        _editor.Document.Changed += (_, e) => _OnDocumentChanged(e);
        _editor.TextArea.AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
        _editor.TextArea.AddHandler(TextInputEvent, _OnTextInput, RoutingStrategies.Tunnel);
        _editor.TextArea.AddHandler(PointerPressedEvent, _OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _blockStyle = new ComboBox
        {
            ItemsSource = new[] { "Text", "Quote", "Code block" },
            SelectedIndex = 0,
            MinHeight = 26,
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            Focusable = false,
            [ToolTip.TipProperty] = "Text style",
        };
        _blockStyle.SelectionChanged += (_, _) => _OnBlockStyleChosen();
        _sourceToggle = new ToggleButton
        {
            Content = "Markdown source",
            Classes = { "Compact" },
            Focusable = false,
            [ToolTip.TipProperty] = "Ctrl+/ — the same text, with every Markdown sign visible",
        };
        _sourceToggle.IsCheckedChanged += (_, _) => ShowSource = _sourceToggle.IsChecked == true;
        _toolbar = _BuildToolbar();

        var page = new Border { Child = _editor };
        page.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));
        DockPanel.SetDock(_toolbar, Dock.Top);
        Content = new DockPanel { Children = { _toolbar, page } };
    }

    public event EventHandler? TextChanged;

    public event Action<int, string>? EditRequested;

    public Control View => this;

    public string Text
    {
        get => _editor.Document.Text;
        set
        {
            _editor.Document.Text = value ?? string.Empty;
            _editor.Document.UndoStack.ClearAll();
        }
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool ShowSource
    {
        get => GetValue(ShowSourceProperty);
        set => SetValue(ShowSourceProperty, value);
    }

    // The line whose Markdown is shown as typed; 0 in the read-only view, where every line is formatted.
    internal int CaretLine => IsReadOnly || ShowSource ? 0 : _caretLine;

    internal int MarkedLine => IsReadOnly ? _markedLine : 0;

    internal TextEditor TextEditor => _editor;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsReadOnlyProperty)
        {
            _editor.IsReadOnly = IsReadOnly;
            _editor.TextArea.Caret.CaretBrush = IsReadOnly ? Brushes.Transparent : null;
            _toolbar.IsVisible = !IsReadOnly;
            _Redraw();
        }
        else if (change.Property == ShowSourceProperty)
        {
            _sourceToggle.IsChecked = ShowSource;
            _editor.FontFamily = ShowSource ? MonoFont : FontFamily.Default;
            _editor.FontSize = ShowSource ? 12.5 : BodySize;
            _Redraw();
        }
    }

    // A line's text as the classification sees it.
    internal string LineText(int lineNumber) => _editor.Document.GetText(_editor.Document.GetLineByNumber(lineNumber));

    internal MarkdownLine Classify(int lineNumber) => _index.Get(lineNumber);

    internal (int First, int Last) TableAround(int lineNumber) => _index.TableAround(lineNumber);

    internal bool IsTable(int lineNumber) => _index.IsTable(lineNumber);

    internal int FrontMatterEnd => _index.FrontMatterEnd;

    internal BlockCollapser Collapser => _collapser;

    // Moves the caret onto a line, which turns it (or its table, its front matter) back into Markdown.
    internal void EditLine(int lineNumber, bool atEnd = false)
    {
        if (IsReadOnly)
        {
            return;
        }

        var line = _editor.Document.GetLineByNumber(lineNumber);
        _editor.TextArea.Caret.Offset = atEnd ? line.EndOffset : line.Offset;
        _editor.TextArea.Focus();
    }

    internal void ToggleTask(int lineNumber)
    {
        var line = _editor.Document.GetLineByNumber(lineNumber);
        var text = _editor.Document.GetText(line);
        var box = text.IndexOf('[', StringComparison.Ordinal) + 1;
        if (!IsReadOnly && box > 0 && box < text.Length)
        {
            _editor.Document.Replace(line.Offset + box, 1, text[box] == ' ' ? "x" : " ");
        }
    }

    public void ApplyBlock(MarkdownBlockStyle style) => _ReplaceCaretLine(text => (MarkdownEdits.SetBlock(text, style), -1, 0));

    public void ApplyWrap(MarkdownWrapStyle style)
    {
        var line = _editor.Document.GetLineByNumber(_caretLine);
        var start = Math.Clamp(_editor.SelectionStart - line.Offset, 0, line.Length);
        var end = Math.Clamp(_editor.SelectionStart + _editor.SelectionLength - line.Offset, start, line.Length);
        if (_editor.SelectionLength == 0)
        {
            start = end = Math.Clamp(_editor.CaretOffset - line.Offset, 0, line.Length);
        }

        _ReplaceCaretLine(text =>
        {
            var wrapped = MarkdownEdits.Wrap(text, start, end, style);
            return (wrapped.Line, wrapped.SelectionStart, wrapped.SelectionLength);
        });
    }

    public void InsertLines(string[] lines, int caretLineInLines)
    {
        if (IsReadOnly)
        {
            return;
        }

        var line = _editor.Document.GetLineByNumber(_caretLine);
        var newLine = _NewLine(line);
        _editor.Document.Insert(line.EndOffset, newLine + string.Join(newLine, lines));
        EditLine(_caretLine + 1 + caretLineInLines, atEnd: true);
    }

    private void _ReplaceCaretLine(Func<string, (string Text, int SelectionStart, int SelectionLength)> edit)
    {
        if (IsReadOnly)
        {
            return;
        }

        var line = _editor.Document.GetLineByNumber(_caretLine);
        var (text, selectionStart, selectionLength) = edit(_editor.Document.GetText(line));
        _editor.Document.Replace(line.Offset, line.Length, text);
        if (selectionStart < 0)
        {
            _editor.CaretOffset = line.Offset + line.Length;
        }
        else
        {
            _editor.Select(line.Offset + selectionStart, selectionLength);
        }

        _editor.TextArea.Focus();
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.Oem2)
        {
            ShowSource = !ShowSource;
            e.Handled = true;
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        if (ctrl && e.Key is Key.B or Key.I or Key.K)
        {
            ApplyWrap(e.Key switch { Key.B => MarkdownWrapStyle.Bold, Key.I => MarkdownWrapStyle.Italic, _ => MarkdownWrapStyle.Link });
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _editor.SelectionLength == 0)
        {
            e.Handled = _ContinueList();
        }
    }

    private bool _ContinueList()
    {
        var line = _editor.Document.GetLineByNumber(_caretLine);
        if (MarkdownEdits.ContinueList(_editor.Document.GetText(line), _editor.CaretOffset - line.Offset) is not { } split)
        {
            return false;
        }

        var replacement = split.After is null ? split.Before : split.Before + _NewLine(line) + split.After;
        _editor.Document.Replace(line.Offset, line.Length, replacement);
        _editor.CaretOffset = split.After is null ? line.Offset : line.Offset + replacement.Length - split.After.Length + split.CaretInAfter;
        return true;
    }

    private void _OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!IsReadOnly || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        EditRequested?.Invoke(_markedLine > 0 ? _markedLine : _editor.TextArea.Caret.Line, e.Text);
    }

    private void _OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsReadOnly)
        {
            return;
        }

        MarkLine(_editor.GetPositionFromPoint(e.GetPosition(_editor))?.Line ?? _markedLine);
    }

    // The read-only view's marked line: where a click landed, and where a key press asks to start editing.
    internal void MarkLine(int lineNumber)
    {
        _markedLine = lineNumber;
        _Redraw();
    }

    private void _OnCaretMoved()
    {
        var line = _editor.TextArea.Caret.Line;
        _SyncToolbar();
        if (line == _caretLine)
        {
            return;
        }

        _caretLine = line;
        _Redraw();
    }

    private void _OnDocumentChanged(DocumentChangeEventArgs e)
    {
        _collapser.Release(_editor.Document.GetLineByOffset(e.Offset).LineNumber);
        _Redraw();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    // Visible lines only; the editor rebuilds no more than what is on screen.
    private void _Redraw()
    {
        if (ShowSource)
        {
            _collapser.ReleaseAll();
        }
        else
        {
            _collapser.Release(CaretLine);
        }

        _editor.TextArea.TextView.Redraw();
    }

    private void _SyncToolbar()
    {
        _syncing = true;
        _blockStyle.SelectedIndex = _index.Get(_editor.TextArea.Caret.Line).Kind switch
        {
            MarkdownLineKind.Quote => 1,
            MarkdownLineKind.Fence or MarkdownLineKind.Code => 2,
            _ => 0,
        };
        _syncing = false;
    }

    private void _OnBlockStyleChosen()
    {
        if (_syncing)
        {
            return;
        }

        switch (_blockStyle.SelectedIndex)
        {
            case 2:
                InsertLines(MarkdownEdits.CodeBlockLines, 1);
                break;
            default:
                ApplyBlock(_blockStyle.SelectedIndex == 1 ? MarkdownBlockStyle.Quote : MarkdownBlockStyle.Text);
                break;
        }

        _SyncToolbar();
    }

    // A reading column of at most 760 wide, centred; the scrollbar stays at the edge of the pane.
    private static Thickness _ColumnPadding(double width)
    {
        var side = Math.Max(32, (width - 760) / 2);
        return new Thickness(side - Gutter.Thickness, 22, side, 60);
    }

    // The file's own line ending, so a CRLF file stays CRLF; only the last line has none, so look upwards.
    private string _NewLine(DocumentLine line)
    {
        for (var candidate = line; candidate is not null; candidate = candidate.PreviousLine)
        {
            if (candidate.DelimiterLength > 0)
            {
                return _editor.Document.GetText(candidate.EndOffset, candidate.DelimiterLength);
            }
        }

        return "\n";
    }

    private Border _BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_blockStyle);
        row.Children.Add(_Button(MaterialIconKind.FormatHeader1, "Heading 1", () => ApplyBlock(MarkdownBlockStyle.Heading1)));
        row.Children.Add(_Button(MaterialIconKind.FormatHeader2, "Heading 2", () => ApplyBlock(MarkdownBlockStyle.Heading2)));
        row.Children.Add(_Button(MaterialIconKind.FormatHeader3, "Heading 3", () => ApplyBlock(MarkdownBlockStyle.Heading3)));
        row.Children.Add(_Separator());
        row.Children.Add(_Button(MaterialIconKind.FormatBold, "Bold (Ctrl+B)", () => ApplyWrap(MarkdownWrapStyle.Bold)));
        row.Children.Add(_Button(MaterialIconKind.FormatItalic, "Italic (Ctrl+I)", () => ApplyWrap(MarkdownWrapStyle.Italic)));
        row.Children.Add(_Button(MaterialIconKind.CodeTags, "Inline code", () => ApplyWrap(MarkdownWrapStyle.Code)));
        row.Children.Add(_Separator());
        row.Children.Add(_Button(MaterialIconKind.FormatListBulleted, "Bulleted list", () => ApplyBlock(MarkdownBlockStyle.Bullet)));
        row.Children.Add(_Button(MaterialIconKind.FormatListNumbered, "Numbered list", () => ApplyBlock(MarkdownBlockStyle.Numbered)));
        row.Children.Add(_Button(MaterialIconKind.FormatListChecks, "Task list", () => ApplyBlock(MarkdownBlockStyle.Task)));
        row.Children.Add(_Separator());
        row.Children.Add(_Button(MaterialIconKind.LinkVariant, "Link (Ctrl+K)", () => ApplyWrap(MarkdownWrapStyle.Link)));
        row.Children.Add(_Button(MaterialIconKind.TablePlus, "Insert table", () => InsertLines(MarkdownEdits.TableLines, 3)));

        var hint = new TextBlock
        {
            Text = "The line you are on shows its Markdown · Ctrl+/ shows all of it",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        DockPanel.SetDock(row, Dock.Left);
        DockPanel.SetDock(_sourceToggle, Dock.Right);
        var bar = new Border
        {
            Padding = new Thickness(14, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { row, _sourceToggle, hint } },
        };
        bar.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitChromeBgBrush"));
        bar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineSoftBrush"));
        return bar;
    }

    private Button _Button(MaterialIconKind icon, string tip, Action action)
    {
        var button = new Button
        {
            Content = new MaterialIcon { Kind = icon, Width = 16, Height = 16 },
            Classes = { "Subtle", "Compact" },
            MinWidth = 30,
            Focusable = false,
            [ToolTip.TipProperty] = tip,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private Control _Separator()
    {
        var rule = new Rectangle { Width = 1, Height = 16, Margin = new Thickness(6, 0) };
        rule.Bind(Shape.FillProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        return rule;
    }
}
