using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
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
    private static readonly IBrush CodeBackground = SolidColorBrush.Parse("#232830");
    private static readonly IBrush CodeBlockBackground = SolidColorBrush.Parse("#131519");
    private static readonly IBrush Hairline = SolidColorBrush.Parse("#2C2F37");
    private static readonly IBrush TableHeaderBackground = SolidColorBrush.Parse("#232833");
    private static readonly IBrush Accent = SolidColorBrush.Parse("#D97757");
    private static readonly IBrush TextPrimary = SolidColorBrush.Parse("#E6E7EA");
    private static readonly IBrush TextSecondary = SolidColorBrush.Parse("#9498A3");
    private static readonly IBrush TextFaint = SolidColorBrush.Parse("#6B6F79");

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Content = _Build(Markdown ?? string.Empty);
        }
    }

    private static Control _Build(string markdown)
    {
        var root = new StackPanel { Spacing = 2 };
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            root.Children.Add(_RenderBlock(block));
        }

        return root;
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
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Bold:
                    run.FontWeight = FontWeight.SemiBold;
                    break;
                case MarkdownInlineKind.Italic:
                    run.FontStyle = FontStyle.Italic;
                    break;
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
                _OpenUrl(link.Url);
                return;
            }
        }
    }

    private static void _OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort: a failed browser launch must not crash the UI thread.
        }
    }
}
