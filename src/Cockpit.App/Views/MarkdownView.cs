using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Core.Markdown;

namespace Cockpit.App.Views;

/// <summary>
/// Renders a markdown string into themed Avalonia controls — the cockpit's own thin markdown layer,
/// replacing Markdown.Avalonia so the transcript look (flat text, calm inline-code, dark tables) and
/// clickable links are fully under our control. Parsing lives in <see cref="MarkdownParser"/>; this
/// control walks the parsed blocks and builds the visual tree, matching the approved look&amp;feel mockup.
/// </summary>
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

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
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

    // The rendered tree is kept and reconciled rather than thrown away, so the rate limit caps how OFTEN a repaint
    // happens and this caps how MUCH each one costs. A delta only ever changes the last block or adds one after
    // it; every block before that compares equal and keeps the controls it already has. Without this a repaint is
    // O(reply length) and a long answer is still quadratic overall, only at 30 fps instead of per delta.
    private readonly StackPanel _blocks = new() { Spacing = 2 };
    private IReadOnlyList<MarkdownBlock> _rendered = [];
    private object? _renderedPalette;

    public MarkdownView() => Content = _blocks;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownProperty)
        {
            return;
        }

        if (_rebuildTimer is { IsEnabled: true })
        {
            _pendingRebuild = true;
            return;
        }

        _Render(Markdown ?? string.Empty);

        _rebuildTimer ??= _CreateRebuildTimer();
        _rebuildTimer.Start();
    }

    private void _Render(string markdown)
    {
        var parsed = MarkdownParser.Parse(markdown);

        // Kept blocks keep the brushes they were built with, so a theme swap has to discard all of them —
        // otherwise the untouched part of a message stays in the previous palette while the rest moves.
        // Avalonia hands out the same brush instance for a key until the theme changes, so identity is the signal.
        var palette = _CurrentPalette();
        if (!ReferenceEquals(palette, _renderedPalette))
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
            else if (!_rendered[i].Equals(parsed[i]))
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

    /// <summary>
    /// A brush instance standing in for the whole palette, for reference comparison only. Null where there are no
    /// application resources at all (design-time preview): nothing to compare, so nothing is ever discarded.
    /// </summary>
    private static object? _CurrentPalette() =>
        Application.Current is { } app && app.TryGetResource("CockpitTextPrimaryBrush", null, out var brush)
            ? brush
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

    private static Control _RenderBlock(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => _Heading(block),
        MarkdownBlockKind.CodeBlock => _CodeBlock(block),
        MarkdownBlockKind.List => _List(block),
        MarkdownBlockKind.Table => _Table(block),
        _ => _Paragraph(block.Inlines, new Thickness(0, 3, 0, 3)),
    };

    private static Control _Heading(MarkdownBlock block)
    {
        var size = block.HeadingLevel switch { 1 => 16.0, 2 => 15.0, 3 => 13.5, _ => 13.0 };
        var text = _InlineTextBlock(block.Inlines);
        text.FontSize = size;
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, 10, 0, 4);
        return text;
    }

    private static Control _Paragraph(IReadOnlyList<MarkdownInline> inlines, Thickness margin)
    {
        var text = _InlineTextBlock(inlines);
        text.Margin = margin;
        return text;
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

        var copy = _CopyButton(block.Code);

        var grid = new Grid();
        grid.Children.Add(scroller);
        grid.Children.Add(copy);

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

        return new Border
        {
            Background = CodeBlockBackground,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Child = grid,
        };
    }

    private static Button _CopyButton(string textToCopy)
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
                await clipboard.SetTextAsync(textToCopy);
            }
        };
        return copy;
    }

    private static Control _List(MarkdownBlock block)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        for (var index = 0; index < block.Items.Count; index++)
        {
            var marker = new TextBlock
            {
                Text = block.Ordered ? $"{index + 1}." : "•",
                Foreground = TextSecondary,
                Margin = new Thickness(6, 0, 8, 0),
                MinWidth = 16,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var content = _InlineTextBlock(block.Items[index]);
            // A DockPanel, not a horizontal StackPanel: a horizontal StackPanel measures its children with
            // infinite available width, so TextWrapping=Wrap on the content never triggers and long list items
            // (e.g. with inline-code tokens) run off and get clipped by the viewport. Docking the marker left
            // and letting the content fill the remainder gives the text a bounded width, so it wraps (AC-144).
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control _Table(MarkdownBlock block)
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

    private static void _AddTableRow(Grid grid, IReadOnlyList<IReadOnlyList<MarkdownInline>> cells, int rowIndex, bool isHeader)
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

    /// <summary>Builds a selectable text block from inline runs, styling code/links and making links clickable.</summary>
    private static SelectableTextBlock _InlineTextBlock(IReadOnlyList<MarkdownInline> inlines)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimary,
            FontSize = 13,
        };

        var links = new List<(int Start, int Length, string Url)>();
        var offset = 0;

        foreach (var inline in inlines)
        {
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
                    break;
                case MarkdownInlineKind.Link:
                    run.Foreground = Accent;
                    run.TextDecorations = TextDecorations.Underline;
                    if (!string.IsNullOrEmpty(inline.Url))
                    {
                        links.Add((offset, inline.Text.Length, inline.Url));
                    }

                    break;
            }

            block.Inlines?.Add(run);
            offset += inline.Text.Length;
        }

        if (links.Count > 0)
        {
            block.Cursor = new Cursor(StandardCursorType.Hand);
            block.PointerReleased += (_, e) => _OnLinkClick(block, links, e);
        }

        return block;
    }

    private static void _OnLinkClick(
        SelectableTextBlock block, List<(int Start, int Length, string Url)> links, PointerReleasedEventArgs e)
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
            if (position >= link.Start && position < link.Start + link.Length)
            {
                ExternalLink.TryOpen(link.Url);
                return;
            }
        }
    }

}
