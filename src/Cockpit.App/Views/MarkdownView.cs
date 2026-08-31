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

// Renders markdown into themed Avalonia controls — replaces Markdown.Avalonia so the transcript
// look and clickable links are fully under our control. Parsing lives in MarkdownParser; this
// walks the parsed blocks and builds the visual tree.
public sealed class MarkdownView : ContentControl
{
    private static readonly FontFamily MonoFont =
        new("Cascadia Mono, Noto Sans Mono, DejaVu Sans Mono, monospace");

    // Resolved through ThemeBrush on every access, not cached statically, so a theme swap is picked
    // up next render instead of staying pinned. Code chip/table header share CockpitInsetBgBrush.
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

    public static readonly StyledProperty<bool> StartsInsideCodeBlockProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(StartsInsideCodeBlock));

    // AC-1265: this text carries on a fence an earlier row opened. Set, the parser starts inside that fence
    // and the box drawn for it loses its top edge, so the two halves read as one block rather than two.
    public bool StartsInsideCodeBlock
    {
        get => GetValue(StartsInsideCodeBlockProperty);
        set => SetValue(StartsInsideCodeBlockProperty, value);
    }

    public static readonly StyledProperty<bool> EndsInsideCodeBlockProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(EndsInsideCodeBlock));

    // The other half of the pair: this text's last fence is still open and the next row carries it on, so its
    // box loses its bottom edge. Told outright rather than read back off the text, because the row that opened
    // a fence and the row that closes it are different rows and each only knows its own side.
    public bool EndsInsideCodeBlock
    {
        get => GetValue(EndsInsideCodeBlockProperty);
        set => SetValue(EndsInsideCodeBlockProperty, value);
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

    // AC-1033: how a picture on its own line is drawn, set by the knowledge base and by nothing else — null
    // leaves an image reference rendering as the link it always was, which is what keeps the chat unchanged.
    // Assign it before `Markdown`: a plain property, so setting it later forces no repaint.
    public Func<MarkdownBlock, Control>? ImageRenderer { get; set; }

    // First refusal on a clicked link, before the browser or the file preview is offered it. The knowledge
    // base claims its own `help:` cross-references this way, so following one moves within the window instead
    // of being handed to the shell. Return true to say the link was handled. Null everywhere else.
    public Func<string, bool>? LinkHandler { get; set; }

    // A streaming reply re-sets Markdown on every delta; rebuilding the whole block tree each time
    // gets more expensive as the reply grows, saturating the UI thread. Same 30fps cap as TtyView:
    // first change renders at once, a burst coalesces into one repaint per interval, last delta always flushes.
    private const int RebuildIntervalMs = 33;

    private DispatcherTimer? _rebuildTimer;
    private bool _pendingRebuild;

    // AC-1262: tells a view being recycled away apart from one that was never attached. Both are "not attached"
    // and only the first must defer its render — a plugin's never-attached view still gets a drawn control.
    private bool _everAttached;
    private TaskCompletionSource? _pendingRebuildSignal;

    // Exposed for tests only (AC-1014): a change coalesced into the rebuild timer renders on its next tick rather
    // than inline, so a test needs the real completion rather than a fixed sleep it hopes outlasted the 33 ms tick.
    // Already-rendered (no timer running yet, or nothing pending) returns a completed task.
    internal Task WaitForPendingRenderAsync() =>
        _pendingRebuild ? (_pendingRebuildSignal ??= new TaskCompletionSource()).Task : Task.CompletedTask;

    // Exposed for tests only (AC-1129): counts full-tree rebuilds, so a test can assert a settling
    // FilePathResolver probe no longer triggers one instead of inferring it from allocation size.
    internal int DebugRenderCount { get; private set; }

    // Tree is kept and reconciled, not thrown away: the rate limit caps how OFTEN a repaint
    // happens, this caps how MUCH each costs. A delta only changes/adds the last block; without
    // this, a repaint is O(reply length) — quadratic overall, just at 30fps instead of per delta.
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
            && change.Property != PreserveLineBreaksProperty
            && change.Property != StartsInsideCodeBlockProperty
            && change.Property != EndsInsideCodeBlockProperty)
        {
            return;
        }

        // AC-1262: a recycled row tears its bindings down inside the layout pass, and the source falling away
        // arrives here as a change. Rebuilding the whole tree of a control being discarded is what stopped the
        // pass converging; the render is deferred, not dropped — OnAttachedToVisualTree pays it on reuse.
        if (_everAttached && !this.IsAttachedToVisualTree())
        {
            _pendingRebuild = true;
            return;
        }

        if (_rebuildTimer is { IsEnabled: true })
        {
            _pendingRebuild = true;
            return;
        }

        _Render(Markdown ?? string.Empty);

        // A view off screen must not start a timer: a running DispatcherTimer roots it until its
        // own idle-stop tick runs on the UI thread. Measured: 40/40 never-attached views stayed
        // alive this way, ~5MB each. Rendering still happens — a plugin gets a drawn control.
        if (this.IsAttachedToVisualTree())
        {
            _rebuildTimer ??= _CreateRebuildTimer();
            _rebuildTimer.Start();
        }
    }

    private void _Render(string markdown)
    {
        DebugRenderCount++;
        var parsed = MarkdownParser.Parse(markdown, PreserveLineBreaks, StartsInsideCodeBlock);

        // Compares the colour, not brush identity: a recycled row's resource lookup returns a
        // different brush instance for an unchanged palette, so identity comparison discarded the
        // whole message every recycle (measured: 21, 23, 25, 27… discards per streaming turn).
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
                _blocks.Children.Add(_RenderBlock(parsed[i], i, parsed.Count));
                continue;
            }

            // AC-1265: the edges a neighbouring row carries on are not part of the block, so a fragment whose
            // neighbour has arrived since needs rebuilding even when its own markdown is unchanged -- the
            // in-place update below keeps the border, and with it the rounding this is trying to take off.
            var edgesMoved = _blocks.Children[i] is CodeBlockBorder edged
                && (edged.JoinedAbove != (StartsInsideCodeBlock && i == 0)
                    || edged.JoinedBelow != (EndsInsideCodeBlock && i == parsed.Count - 1));

            if (edgesMoved
                || (!_rendered[i].Equals(parsed[i])
                    && !_TryUpdateInPlace((Control)_blocks.Children[i], _rendered[i], parsed[i])))
            {
                _blocks.Children[i] = _RenderBlock(parsed[i], i, parsed.Count);
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
        _everAttached = true;

        // Recycled back onto a row whose text moved on while it was scrolled away: render the current text once,
        // rather than showing the stale tree until the next delta happens to arrive.
        if (_pendingRebuild)
        {
            _pendingRebuild = false;
            _Render(Markdown ?? string.Empty);
        }
    }

    // AC-1129: patches just the run whose probe landed instead of _Render-ing the whole tree. `epoch`
    // skips a stale patch if the block was refilled with different content while the probe was in flight.
    private void _OnPathResolved(InlineTextBlock block, int epoch, Run run, int offset, int length, string candidatePath, string? basePath, int? line)
    {
        if (block.Epoch != epoch || FilePathResolver.Resolve(candidatePath, basePath, static () => { }) is not { } resolvedPath)
        {
            return;
        }

        run.Foreground = ClickablePathForeground;
        run.Background = ClickablePathBackground;
        block.Links.Add((offset, length, resolvedPath, line));
        block.Cursor = _handCursor ??= new Cursor(StandardCursorType.Hand);
    }

    // AC-1265: only the first block can be the one an earlier row opened, and only the last can be the one the
    // next row carries on -- so the two flags reach `_CodeBlock` as the edges it must leave off.
    private Control _RenderBlock(MarkdownBlock block, int index, int count) => block.Kind switch
    {
        MarkdownBlockKind.Heading => _Heading(block),
        MarkdownBlockKind.CodeBlock => _CodeBlock(
            block,
            joinedAbove: StartsInsideCodeBlock && index == 0,
            joinedBelow: EndsInsideCodeBlock && index == count - 1,
            owner: this),
        MarkdownBlockKind.List => _List(block),
        MarkdownBlockKind.Table => _Table(block),
        MarkdownBlockKind.Image => _Image(block),
        _ => _Paragraph(block.Inlines, new Thickness(0, 3, 0, 3)),
    };

    // AC-1033: with no renderer, back to the tree this block produced before it had a kind of its own. Not
    // left to the paragraph fallback, which reads `Inlines` — empty here, so the chat would have gone blank.
    private Control _Image(MarkdownBlock block) =>
        ImageRenderer?.Invoke(block)
        ?? _Paragraph(
            MarkdownParser.ParseInlines($"![{block.ImageAlt}]({block.ImageSource})", PreserveLineBreaks),
            new Thickness(0, 3, 0, 3));

    // Updates the block's existing control, for shapes that arrive as one big block (table/fence)
    // where block-level reuse is too coarse. Returns false when shape changed rather than grew.
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
                       && was.OrderedStart == now.OrderedStart
                       && control is StackPanel list
                       && _UpdateListItems(list, was.Items, now.Items, now.Ordered, now.OrderedStart);

            case MarkdownBlockKind.Table:
                return control is Border { Child: Grid grid } && _UpdateTableRows(grid, was, now);

            // Never updated in place: an image does not stream, and the paragraph fallback below would refill
            // this control from `Inlines`, which an image block leaves empty — blanking what was drawn.
            case MarkdownBlockKind.Image:
                return false;

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
        bool ordered,
        int orderedStart)
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
                panel.Children.Add(_ListRow(now[i], orderedStart + i, ordered));
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

        // AC-1265: the view this box belongs to, so Copy can ask it for the whole block at click time — a
        // string captured here would be short by whatever arrived since.
        public required MarkdownView Owner { get; init; }

        // Which edges a neighbouring row carries on, so a render that finds them changed rebuilds the box:
        // the in-place update keeps the border, and with it the rounding this is meant to take off.
        public bool JoinedAbove;

        public bool JoinedBelow;
    }

    // `joinedAbove`/`joinedBelow`: this box carries on, or is carried on by, a box in a neighbouring row. The
    // touching edge loses its rounding and its margin, so a fence split across rows reads as one block.
    private static Control _CodeBlock(MarkdownBlock block, bool joinedAbove, bool joinedBelow, MarkdownView owner)
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
            Owner = owner,
            JoinedAbove = joinedAbove,
            JoinedBelow = joinedBelow,
            Background = CodeBlockBackground,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1, joinedAbove ? 0 : 1, 1, joinedBelow ? 0 : 1),
            CornerRadius = new CornerRadius(joinedAbove ? 0 : 6, joinedAbove ? 0 : 6, joinedBelow ? 0 : 6, joinedBelow ? 0 : 6),
            Padding = new Thickness(10, joinedAbove ? 0 : 8, 10, joinedBelow ? 0 : 8),
            Margin = new Thickness(0, joinedAbove ? 0 : 6, 0, joinedBelow ? 0 : 6),
            Child = grid,
        };

        // Copy button built on first hover, not with the block: templating it costs more than the
        // rest of a fenced block combined (measured: 290KB vs 5KB), and nobody clicks it without hovering first.
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
        border.Body.Children.Add(_CopyButton(border));
    }

    // Reads the block it belongs to at click time rather than closing over the text it was built with: a fence
    // that is still streaming replaces that text, and a captured copy would put a truncated body on the clipboard.
    // A fragment of a split fence hands over the whole block instead -- half a code block is worse than no button.
    private static Button _CopyButton(CodeBlockBorder source)
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
                var whole = (source.Owner.DataContext as ISpannedCodeSource)?.SpannedCodeText;
                await clipboard.SetTextAsync(
                    string.IsNullOrEmpty(whole) ? source.Code.Text ?? string.Empty : whole);
            }
        };
        return copy;
    }

    private Control _List(MarkdownBlock block)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        for (var index = 0; index < block.Items.Count; index++)
        {
            panel.Children.Add(_ListRow(block.Items[index], block.OrderedStart + index, block.Ordered));
        }

        return panel;
    }

    private Control _ListRow(IReadOnlyList<MarkdownInline> item, int number, bool ordered)
    {
        var marker = new TextBlock
        {
            Text = ordered ? $"{number}." : "•",
            Foreground = TextSecondary,
            Margin = new Thickness(6, 0, 8, 0),
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // DockPanel, not horizontal StackPanel: a StackPanel measures children with infinite width,
        // so TextWrapping=Wrap never triggers and long list items get clipped (AC-144).
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

    // Keeps the link ranges its runs were built from: refilling one while streaming would otherwise
    // stack another click handler per repaint. `Line` rides along for a resolved file path (AC-642).
    private sealed class InlineTextBlock : SelectableTextBlock
    {
        public readonly List<(int Start, int Length, string Url, int? Line)> Links = [];

        // Bumped by every _FillInlines call on this block — a settling FilePathResolver probe (AC-1129)
        // checks its captured epoch against the current one before patching a run, so it never writes
        // into a block that has since been refilled with different content.
        public int Epoch;

        // Without this, Avalonia's implicit-theme lookup keys on the concrete type (AC-679) and finds no
        // ControlTheme for this private subclass — so it silently gets none at all: no SelectionBrush to paint
        // a selection with, no IBeam cursor, no right-click Copy menu. Styled as SelectableTextBlock instead.
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
    }

    // One cursor per block holding a link, not one per build — a streaming reply otherwise built a
    // fresh platform handle per repaint. Made on first use, not a static initialiser, since tests
    // touch this type without a platform at all.
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
                _OnLinkClick(block, block.Links, e, LinkHandler);
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
        var epoch = ++block.Epoch;

        var links = block.Links;
        var offset = 0;
        var basePath = BasePath;

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
                    if (FilePathCandidate.TryParse(inline.Text, out var candidatePath, out var candidateLine))
                    {
                        var runOffset = offset;
                        var runLength = inline.Text.Length;
                        var resolvedPath = FilePathResolver.Resolve(candidatePath, basePath, () =>
                            _OnPathResolved(block, epoch, run, runOffset, runLength, candidatePath, basePath, candidateLine));
                        if (resolvedPath is not null)
                        {
                            run.Foreground = ClickablePathForeground;
                            run.Background = ClickablePathBackground;
                            links.Add((runOffset, runLength, resolvedPath, candidateLine));
                        }
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

    // A web address opens the browser; anything else in Links is a resolved existing file path —
    // opens the preview window instead. GetTopLevel is asked of `block`, not the enclosing
    // MarkdownView, so this stays usable from a static context without threading `this` through.
    private static void _OnLinkClick(
        SelectableTextBlock block,
        List<(int Start, int Length, string Url, int? Line)> links,
        PointerReleasedEventArgs e,
        Func<string, bool>? handler)
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

            if (handler is not null && handler(link.Url))
            {
                // Claimed by the surface this view sits in — the knowledge base takes its own `help:`
                // cross-references so following one stays inside the window instead of reaching the shell.
            }
            else if (ExternalLink.TryParseWebAddress(link.Url, out var address))
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
