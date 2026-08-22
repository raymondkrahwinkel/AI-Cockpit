using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Core.Markdown;

namespace Cockpit.App.Views;

// Renders a markdown string into themed Avalonia controls — the cockpit's own thin markdown layer,
// replacing Markdown.Avalonia so the transcript look (flat text, calm inline-code, dark tables) and
// clickable links are fully under our control. Parsing lives in `MarkdownParser`; this
// control walks the parsed blocks and builds the visual tree, matching the approved look&amp;feel mockup.
public sealed class MarkdownView : ContentControl
{
    private static readonly FontFamily MonoFont =
        new("Cascadia Mono, Noto Sans Mono, DejaVu Sans Mono, monospace");

    // Resolved through ThemeBrush on every access rather than cached in a static readonly field, so a theme swap
    // is picked up the next time a message renders instead of staying pinned to whatever was current at type-load.
    // The two colours with no dedicated token (code chip, table header) share CockpitInsetBgBrush — the layer
    // above the panel that both chips and the code block sit on.
    private static IBrush CodeBackground => ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430");
    private static IBrush CodeBlockBackground => ThemeBrush.Resolve("CockpitSecondaryBgBrush", "#0c0e12");
    private static IBrush Hairline => ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39");
    private static IBrush TableHeaderBackground => ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430");
    private static IBrush Accent => ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb");
    private static IBrush TextPrimary => ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef");
    private static IBrush TextSecondary => ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5");
    private static IBrush TextFaint => ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78");

    // A clickable path's tint (AC-642): lighter than Accent so a resolved file still reads as code, on the
    // same wash a selected row already uses — see the tokens' own comments in Theme.axaml.
    private static IBrush ClickablePathForeground => ThemeBrush.Resolve("CockpitClickablePathFgBrush", "#93b4f7");
    private static IBrush ClickablePathBackground => ThemeBrush.Resolve("CockpitAccentSelectionBrush", "#292563eb");

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly StyledProperty<string?> BasePathProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(BasePath));

    // The directory a relative code-span path resolves against (AC-642) — the hosting surface's working
    // directory, bound in per view (SessionView.axaml, AssistantChatWindow.axaml). Null where none is known
    // (a plugin dialog, delegated-task output): only absolute paths resolve there.
    public string? BasePath
    {
        get => GetValue(BasePathProperty);
        set => SetValue(BasePathProperty, value);
    }

    public static readonly StyledProperty<bool> PreserveLineBreaksProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(PreserveLineBreaks));

    // Off by default (CommonMark: a single newline joins its paragraph's lines with a space) so file
    // previews and assistant markdown are unaffected. The chat bubble (`TranscriptRowView`) turns this on:
    // a Shift+Enter there is meant to stay a visible line break, not collapse into the words around it (AC-936).
    public bool PreserveLineBreaks
    {
        get => GetValue(PreserveLineBreaksProperty);
        set => SetValue(PreserveLineBreaksProperty, value);
    }

    // A streaming reply re-sets Markdown on every delta, and each set rebuilt the whole block tree: at the end of
    // a long answer that is hundreds of controls reparsed and reconstructed, tens of times a second, for text that
    // grew by a few characters. The cost climbs with the reply, so it accelerates rather than settles — the UI
    // thread saturates and RSS runs away. Same runaway TtyView caps at 30 fps for the terminal, capped the same way
    // here: the first change after a quiet moment renders at once, a burst is coalesced into one repaint per
    // interval, and the tick after the last delta always flushes, so the finished reply is never left stale.
    private const int RebuildIntervalMs = 33;

    private DispatcherTimer? _rebuildTimer;
    private bool _pendingRebuild;
    private TaskCompletionSource? _pendingRebuildSignal;

    // Exposed for tests only (AC-1014): a change coalesced into the rebuild timer renders on its next tick rather
    // than inline, so a test needs the real completion rather than a fixed sleep it hopes outlasted the 33 ms tick.
    // Already-rendered (no timer running yet, or nothing pending) returns a completed task.
    internal Task WaitForPendingRenderAsync() =>
        _pendingRebuild ? (_pendingRebuildSignal ??= new TaskCompletionSource()).Task : Task.CompletedTask;

    // The rendered tree is kept and reconciled rather than thrown away, so the rate limit caps how OFTEN a repaint
    // happens and this caps how MUCH each one costs. A delta only ever changes the last block or adds one after
    // it; every block before that compares equal and keeps the controls it already has. Without this a repaint is
    // O(reply length) and a long answer is still quadratic overall, only at 30 fps instead of per delta.
    private readonly StackPanel _blocks = new() { Spacing = 2 };
    private IReadOnlyList<MarkdownBlock> _rendered = [];
    private Color? _renderedPalette;

    public MarkdownView()
    {
        Content = _blocks;
#if DEBUG
        Cockpit.App.Diagnostics.LeakTracker.Register(this);
#endif
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownProperty
            && change.Property != BasePathProperty
            && change.Property != PreserveLineBreaksProperty)
        {
            return;
        }

        if (_rebuildTimer is { IsEnabled: true })
        {
            _pendingRebuild = true;
            return;
        }

        _Render(Markdown ?? string.Empty);

        // The rate limit is for a view on screen; a view that is not on screen must not start a timer, because a
        // running DispatcherTimer is rooted by the dispatcher and its tick closes over this view — so the view and
        // every control it built stay reachable for as long as it runs. The tick that stops an idle timer is the
        // only thing that releases them, and it runs on the UI thread: exactly the thread that is not keeping up
        // during the heavy streaming reply this rate limit exists for. A virtualising panel binds a container
        // before attaching it and is free to drop it again without ever attaching it, so those views are made in
        // numbers. Measured before this guard: 40 of 40 dropped views stayed alive until the dispatcher got round
        // to their ticks, at roughly 5 MB of controls each — and it feeds itself, since the more is pinned, the
        // further behind the UI thread falls and the longer the next batch stays pinned.
        //
        // Rendering still happens: a plugin asking the host for a markdown control gets one that is drawn, not one
        // waiting for a visual tree it may never be put in.
        if (this.IsAttachedToVisualTree())
        {
            _rebuildTimer ??= _CreateRebuildTimer();
            _rebuildTimer.Start();
        }
    }

    private void _Render(string markdown)
    {
        var parsed = MarkdownParser.Parse(markdown, PreserveLineBreaks);

        // Kept blocks keep the brushes they were built with, so a theme swap has to discard all of them —
        // otherwise the untouched part of a message stays in the previous palette while the rest moves.
        //
        // The colour, not the brush instance. Identity looked like the signal — Avalonia does hand out one brush
        // per key — but a resource lookup is answered against the tree the control is currently in, and a
        // virtualising panel detaches and re-attaches a row as it recycles it. Every recycle therefore came back
        // with a different instance for an unchanged palette and threw the whole message away: measured over eight
        // streaming turns, 0 discards on the first (nothing scrolls yet) and 21, 23, 25, 27 … on the ones after,
        // one per detach, each rebuilding every block of the reply. That is the SDK pane's memory runaway.
        var palette = _CurrentPalette();
        if (palette != _renderedPalette)
        {
            _renderedPalette = palette;
            _rendered = [];
            _blocks.Children.Clear();
        }

        for (var i = 0; i < parsed.Count; i++)
        {
            if (i >= _rendered.Count)
            {
                _blocks.Children.Add(_RenderBlock(parsed[i]));
            }
            else if (!_rendered[i].Equals(parsed[i]) &&
                     !_TryUpdateInPlace((Control)_blocks.Children[i], _rendered[i], parsed[i]))
            {
                _blocks.Children[i] = _RenderBlock(parsed[i]);
            }
        }

        // Markdown that shrank — a row reused for a different message, or an edit rather than an append.
        while (_blocks.Children.Count > parsed.Count)
        {
            _blocks.Children.RemoveAt(_blocks.Children.Count - 1);
        }

        _rendered = parsed;
    }

    // One colour standing in for the whole palette. Null where there are no application resources at all
    // (design-time preview): nothing to compare, so nothing is ever discarded.
    private static Color? _CurrentPalette() =>
        Application.Current is { } app
        && app.TryGetResource("CockpitTextPrimaryBrush", null, out var value)
        && value is ISolidColorBrush brush
            ? brush.Color
            : null;

    private DispatcherTimer _CreateRebuildTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RebuildIntervalMs) };
        timer.Tick += (_, _) =>
        {
            if (_pendingRebuild)
            {
                _pendingRebuild = false;
                _Render(Markdown ?? string.Empty);
                _pendingRebuildSignal?.SetResult();
                _pendingRebuildSignal = null;
                return;
            }

            // Nothing arrived this interval: the stream is idle, so stop ticking and let the next change render
            // straight away. A timer left running would also keep this view — and its row — alive after the
            // virtualising panel recycled it.
            timer.Stop();
        };
        return timer;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _rebuildTimer?.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Recycled back onto a row whose text moved on while it was scrolled away: render the current text once,
        // rather than showing the stale tree until the next delta happens to arrive.
        if (_pendingRebuild)
        {
            _pendingRebuild = false;
            _Render(Markdown ?? string.Empty);
        }
    }

    // `FilePathResolver`'s callback once a background probe lands (AC-642, valkuil 2): `_Render` reuses the
    // block tree and compares parsed blocks for equality, so an answer that only changed a run's brush inside
    // an otherwise-unchanged block is invisible to it. Force one full rebuild of this message to make the
    // now-known answer show up; a view that is not attached defers to the same `_pendingRebuild` path a
    // recycled row already uses.
    private void _OnPathResolved()
    {
        if (this.IsAttachedToVisualTree())
        {
            _rendered = [];
            _blocks.Children.Clear();
            _Render(Markdown ?? string.Empty);
        }
        else
        {
            _pendingRebuild = true;
        }
    }

    private Control _RenderBlock(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => _Heading(block),
        MarkdownBlockKind.CodeBlock => _CodeBlock(block),
        MarkdownBlockKind.List => _List(block),
        MarkdownBlockKind.Table => _Table(block),
        _ => _Paragraph(block.Inlines, new Thickness(0, 3, 0, 3)),
    };

    // Updates the control a block already has instead of building a new one, for the change a stream actually
    // makes: the block at the end grew. Block-level reuse alone is too coarse for the shapes that arrive as one
    // big block — a table or a fence is a single block however long it gets, so every repaint reconstructed its
    // whole grid, or its border, scroller and copy button. Measured over a 4 KB reply that is 231 MB for a table
    // and 188 MB for a fence against 48 MB for the same length of prose, which splits into small blocks of which
    // only the last is rebuilt. Returns false whenever the shape changed rather than grew, and a rebuild is then
    // the honest answer.
    private bool _TryUpdateInPlace(Control control, MarkdownBlock was, MarkdownBlock now)
    {
        if (was.Kind != now.Kind)
        {
            return false;
        }

        switch (now.Kind)
        {
            case MarkdownBlockKind.Heading:
                if (was.HeadingLevel != now.HeadingLevel || control is not InlineTextBlock heading)
                {
                    return false;
                }

                _FillInlines(heading, now.Inlines);
                return true;

            case MarkdownBlockKind.CodeBlock:
                // The language rides on the fence's opening line and is rendered as its own label, so a change
                // there is a different block rather than a longer one.
                if (!string.Equals(was.Language, now.Language, StringComparison.Ordinal) ||
                    control is not CodeBlockBorder code)
                {
                    return false;
                }

                code.Code.Text = now.Code;
                return true;

            case MarkdownBlockKind.List:
                return was.Ordered == now.Ordered
                       && control is StackPanel list
                       && _UpdateListItems(list, was.Items, now.Items, now.Ordered);

            case MarkdownBlockKind.Table:
                return control is Border { Child: Grid grid } && _UpdateTableRows(grid, was, now);

            default:
                if (control is not InlineTextBlock paragraph)
                {
                    return false;
                }

                _FillInlines(paragraph, now.Inlines);
                return true;
        }
    }

    private bool _UpdateListItems(
        StackPanel panel,
        IReadOnlyList<IReadOnlyList<MarkdownInline>> was,
        IReadOnlyList<IReadOnlyList<MarkdownInline>> now,
        bool ordered)
    {
        if (panel.Children.Count != was.Count)
        {
            return false;
        }

        var shared = 0;
        while (shared < was.Count && shared < now.Count && _SameInlines(was[shared], now[shared]))
        {
            shared++;
        }

        for (var i = shared; i < now.Count; i++)
        {
            if (i >= panel.Children.Count)
            {
                panel.Children.Add(_ListRow(now[i], i, ordered));
                continue;
            }

            if (panel.Children[i] is not DockPanel { Children: [_, InlineTextBlock content] })
            {
                return false;
            }

            _FillInlines(content, now[i]);
        }

        while (panel.Children.Count > now.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }

        return true;
    }

    private bool _UpdateTableRows(Grid grid, MarkdownBlock was, MarkdownBlock now)
    {
        // The header sets the columns, so a change there is a different table rather than a longer one.
        if (!_SameCells(was.Items, now.Items))
        {
            return false;
        }

        var shared = 0;
        while (shared < was.Rows.Count && shared < now.Rows.Count && _SameCells(was.Rows[shared], now.Rows[shared]))
        {
            shared++;
        }

        // Dropped by the row each cell is placed in rather than by counting: a row still being typed is ragged,
        // so cells-per-row is not a number to do arithmetic with. Row 0 is the header and always stays.
        for (var i = grid.Children.Count - 1; i >= 0; i--)
        {
            if (Grid.GetRow(grid.Children[i]) > shared)
            {
                grid.Children.RemoveAt(i);
            }
        }

        while (grid.RowDefinitions.Count > shared + 1)
        {
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        }

        for (var r = shared; r < now.Rows.Count; r++)
        {
            _AddTableRow(grid, now.Rows[r], r + 1, isHeader: false);
        }

        return true;
    }

    private static bool _SameCells(
        IReadOnlyList<IReadOnlyList<MarkdownInline>> a, IReadOnlyList<IReadOnlyList<MarkdownInline>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!_SameInlines(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool _SameInlines(IReadOnlyList<MarkdownInline> a, IReadOnlyList<MarkdownInline> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private Control _Heading(MarkdownBlock block)
    {
        var size = block.HeadingLevel switch { 1 => 16.0, 2 => 15.0, 3 => 13.5, _ => 13.0 };
        var text = _InlineTextBlock(block.Inlines);
        text.FontSize = size;
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, 10, 0, 4);
        return text;
    }

    private Control _Paragraph(IReadOnlyList<MarkdownInline> inlines, Thickness margin)
    {
        var text = _InlineTextBlock(inlines);
        text.Margin = margin;
        return text;
    }

    // A code block that keeps hold of its text, so a fence still streaming can have its body replaced
    // rather than the whole border, scroller and copy button built again for every repaint.
    private sealed class CodeBlockBorder : Border
    {
        public required SelectableTextBlock Code { get; init; }

        // Where the copy button goes once the pointer asks for it — see `_CodeBlock`.
        public required Grid Body { get; init; }

        public bool HasCopyButton;
    }

    private static Control _CodeBlock(MarkdownBlock block)
    {
        var code = new SelectableTextBlock
        {
            Text = block.Code,
            FontFamily = MonoFont,
            FontSize = 12.5,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            Content = code,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var grid = new Grid();
        grid.Children.Add(scroller);

        if (!string.IsNullOrEmpty(block.Language))
        {
            var lang = new TextBlock
            {
                Text = block.Language,
                FontFamily = MonoFont,
                FontSize = 10,
                Foreground = TextFaint,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 52, 0),
            };
            grid.Children.Add(lang);
        }

        var border = new CodeBlockBorder
        {
            Code = code,
            Body = grid,
            Background = CodeBlockBackground,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Child = grid,
        };

        // The copy button is built on the first hover, not with the block. A Button is a templated control, and
        // applying its theme costs more than the border, scroller and text of a fenced block put together —
        // measured, a fence is 290 KB to build against 5 KB for a paragraph, and a transcript row is rebuilt
        // every time the virtualising panel realises it again. Nobody can click a button without first moving a
        // pointer onto the block, so nothing is lost by waiting for that.
        border.PointerEntered += _AddCopyButton;
        return border;
    }

    private static void _AddCopyButton(object? sender, PointerEventArgs e)
    {
        if (sender is not CodeBlockBorder { HasCopyButton: false } border)
        {
            return;
        }

        border.HasCopyButton = true;
        border.PointerEntered -= _AddCopyButton;
        border.Body.Children.Add(_CopyButton(border.Code));
    }

    // Reads the block it belongs to at click time rather than closing over the text it was built with: a fence
    // that is still streaming replaces that text, and a captured copy would put a truncated body on the clipboard.
    private static Button _CopyButton(SelectableTextBlock source)
    {
        var copy = new Button
        {
            Content = "Copy",
            FontSize = 10,
            Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(copy)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(source.Text ?? string.Empty);
            }
        };
        return copy;
    }

    private Control _List(MarkdownBlock block)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        for (var index = 0; index < block.Items.Count; index++)
        {
            panel.Children.Add(_ListRow(block.Items[index], index, block.Ordered));
        }

        return panel;
    }

    private Control _ListRow(IReadOnlyList<MarkdownInline> item, int index, bool ordered)
    {
        var marker = new TextBlock
        {
            Text = ordered ? $"{index + 1}." : "•",
            Foreground = TextSecondary,
            Margin = new Thickness(6, 0, 8, 0),
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // A DockPanel, not a horizontal StackPanel: a horizontal StackPanel measures its children with
        // infinite available width, so TextWrapping=Wrap on the content never triggers and long list items
        // (e.g. with inline-code tokens) run off and get clipped by the viewport. Docking the marker left
        // and letting the content fill the remainder gives the text a bounded width, so it wraps (AC-144).
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        row.Children.Add(_InlineTextBlock(item));
        return row;
    }

    private Control _Table(MarkdownBlock block)
    {
        var columns = block.Items.Count;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        _AddTableRow(grid, block.Items, rowIndex++, isHeader: true);
        foreach (var row in block.Rows)
        {
            _AddTableRow(grid, row, rowIndex++, isHeader: false);
        }

        return new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
    }

    private void _AddTableRow(Grid grid, IReadOnlyList<IReadOnlyList<MarkdownInline>> cells, int rowIndex, bool isHeader)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < cells.Count; c++)
        {
            var text = _InlineTextBlock(cells[c]);
            text.Foreground = isHeader ? TextPrimary : TextSecondary;
            if (isHeader)
            {
                text.FontWeight = FontWeight.SemiBold;
            }

            var cell = new Border
            {
                Background = isHeader ? TableHeaderBackground : Brushes.Transparent,
                BorderBrush = Hairline,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 6),
                Child = text,
            };
            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }

    // A text block that keeps the link ranges its runs were built from. Refilling one while a reply streams
    // would otherwise stack another click handler on it per repaint, and hand out another platform cursor.
    // `Line` rides along for a resolved file path (AC-642) — null for an ordinary markdown link, which has
    // nowhere to jump to.
    private sealed class InlineTextBlock : SelectableTextBlock
    {
        public readonly List<(int Start, int Length, string Url, int? Line)> Links = [];

        // Without this, Avalonia's implicit-theme lookup keys on the concrete type (AC-679) and finds no
        // ControlTheme for this private subclass — so it silently gets none at all: no SelectionBrush to paint
        // a selection with, no IBeam cursor, no right-click Copy menu. Styled as SelectableTextBlock instead.
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
    }

    // One cursor for every block that holds a link, rather than one per build: a Cursor is a platform handle, and
    // a streaming reply built a fresh one on each of its repaints. Made on first use rather than in a static
    // initialiser: constructing one asks the platform for a cursor, and the type is touched by tests that run
    // without a platform at all — an initialiser would take those down on the mere mention of this class.
    private static Cursor? _handCursor;

    // Builds a selectable text block from inline runs, styling code/links and making links clickable.
    private InlineTextBlock _InlineTextBlock(IReadOnlyList<MarkdownInline> inlines)
    {
        var block = new InlineTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimary,
            FontSize = 13,
        };

        // Attached once, for the life of the block: the handler reads the link list rather than closing over
        // the ranges of one particular fill.
        block.PointerReleased += (_, e) =>
        {
            if (block.Links.Count > 0)
            {
                _OnLinkClick(block, block.Links, e);
            }
        };

        _FillInlines(block, inlines);
        return block;
    }

    // Replaces a block's runs in place — what a growing paragraph, list item or table cell needs.
    private void _FillInlines(InlineTextBlock block, IReadOnlyList<MarkdownInline> inlines)
    {
        block.Links.Clear();
        block.Inlines?.Clear();

        var links = block.Links;
        var offset = 0;

        foreach (var inline in inlines)
        {
            if (inline.Kind == MarkdownInlineKind.LineBreak)
            {
                block.Inlines?.Add(new LineBreak());
                continue;
            }

            var run = new Run(inline.Text);

            // Asked of the run rather than switched on its kind: emphasis around a link or a code span rides
            // along as a flag, so a bold link is one run that is both — not a bold run holding a link.
            if (inline.IsBold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (inline.IsItalic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            switch (inline.Kind)
            {
                case MarkdownInlineKind.Code:
                    run.FontFamily = MonoFont;
                    run.Background = CodeBackground;

                    // A code span that could be a file path (AC-642): the cheap vorm filter decides who gets
                    // asked, the memoised resolver — never on this thread — decides who is real. Still unknown
                    // or not a file: the run stays plain code, exactly as before.
                    if (FilePathCandidate.TryParse(inline.Text, out var candidatePath, out var candidateLine) &&
                        FilePathResolver.Resolve(candidatePath, BasePath, _OnPathResolved) is { } resolvedPath)
                    {
                        run.Foreground = ClickablePathForeground;
                        run.Background = ClickablePathBackground;
                        links.Add((offset, inline.Text.Length, resolvedPath, candidateLine));
                    }

                    break;
                case MarkdownInlineKind.Link:
                    run.Foreground = Accent;
                    run.TextDecorations = TextDecorations.Underline;
                    if (!string.IsNullOrEmpty(inline.Url))
                    {
                        links.Add((offset, inline.Text.Length, inline.Url, null));
                    }

                    break;
            }

            block.Inlines?.Add(run);
            offset += inline.Text.Length;
        }

        block.Cursor = links.Count > 0 ? _handCursor ??= new Cursor(StandardCursorType.Hand) : null;
    }

    // A web address opens the browser exactly as before; anything else in `Links` is a file path this same
    // method already resolved to a real, existing file — open the preview window instead. `TopLevel.GetTopLevel`
    // is asked of `block` rather than the enclosing `MarkdownView`: both sit in the same window, and this stays
    // usable from a static context without threading `this` through the click handler.
    private static void _OnLinkClick(
        SelectableTextBlock block, List<(int Start, int Length, string Url, int? Line)> links, PointerReleasedEventArgs e)
    {
        // Selecting text also raises PointerReleased; only treat it as a link click when nothing is selected.
        if (block.SelectionEnd != block.SelectionStart)
        {
            return;
        }

        var hit = block.TextLayout.HitTestPoint(e.GetPosition(block));
        var position = hit.TextPosition;
        foreach (var link in links)
        {
            if (position < link.Start || position >= link.Start + link.Length)
            {
                continue;
            }

            if (ExternalLink.TryParseWebAddress(link.Url, out var address))
            {
                ExternalLink.TryOpen(address);
            }
            else if (TopLevel.GetTopLevel(block) is Window owner)
            {
                FilePreviewWindow.Show(link.Url, link.Line, owner);
            }

            return;
        }
    }

}
