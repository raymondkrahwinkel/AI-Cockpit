using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.SessionReview;

/// <summary>
/// The per-session review panel (AC-50): the uncommitted changes of the session's working directory as a tree of
/// changed files on the left and one file's diff on the right, with one click to ask the session to review its own
/// changes or to copy the whole diff. Read-only and operator-triggered — no consent gate.
/// </summary>
/// <remarks>
/// The panel used to draw the entire diff as one flat list of coloured strings, headers and all, which made a review
/// of more than one file an exercise in scrolling for the next <c>diff --git</c> line. <see cref="DiffParser"/> now
/// recovers the structure git had already written into that text, and this control only lays it out.
/// </remarks>
internal sealed class SessionDiffDialogControl : UserControl
{
    /// <summary>A large file is for scanning, not for rendering ten thousand text blocks; cap what is drawn and say
    /// so. The cap is per file now rather than over the whole diff, so one huge file no longer hides every file
    /// after it.</summary>
    private const int MaxRenderedLines = 2000;

    private const double GutterWidth = 46;

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,DejaVu Sans Mono,monospace");

    private readonly ICockpitHost _host;
    private readonly IPluginSessionContext _session;
    private readonly GitDiffReader _reader = new();

    private readonly TextBlock _header = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totals = new() { FontSize = 12, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center };
    private readonly TreeView _tree;
    private readonly TextBlock _pathDirectory = new() { FontSize = 12, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pathName = new() { FontSize = 12, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pathKind = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ScrollViewer _rowScroll;
    private readonly Button _review;
    private readonly Button _copy;

    private string _diff = string.Empty;
    private string _branch = string.Empty;

    public SessionDiffDialogControl(ICockpitHost host, IPluginSessionContext session)
    {
        _host = host;
        _session = session;

        _review = new Button { Content = "Ask this session to review", IsEnabled = false };
        _review.Click += async (_, _) => await _ReviewAsync();

        _copy = new Button { Content = "Copy diff", IsEnabled = false };
        _copy.Click += async (_, _) => await _CopyAsync();

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => _ = _LoadAsync();

        _tree = new TreeView
        {
            Background = _Brush("CockpitSecondaryBgBrush", "#0c0e12"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            ItemTemplate = new FuncTreeDataTemplate<TreeNode>((node, _) => _NodeRow(node), node => node.Children),
        };

        // Folders open by default: a review is short-lived and the operator came here to see everything at once,
        // not to click a repository's directory structure open one level at a time.
        _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) },
        });
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is TreeNode { File: { } file })
            {
                _ShowFile(file);
            }
        };

        _rowScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _rows,
        };

        Content = new DockPanel
        {
            Children =
            {
                _Docked(_HeaderBar(), Dock.Top),
                _Docked(_Footer(refresh), Dock.Bottom),
                _Body(),
            },
        };

        _ = _LoadAsync();
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private Control _HeaderBar() => new Border
    {
        Padding = new Thickness(12, 9, 12, 9),
        BorderThickness = new Thickness(0, 0, 0, 1),
        BorderBrush = _Brush("CockpitHairlineSoftBrush", "#20242c"),
        Child = new DockPanel { Children = { _Docked(_totals, Dock.Right), _header } },
    };

    private Control _Footer(Button refresh) => new Border
    {
        Padding = new Thickness(12, 9, 12, 9),
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = _Brush("CockpitHairlineSoftBrush", "#20242c"),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _review, _copy, refresh },
        },
    };

    private Control _Body()
    {
        var splitter = new GridSplitter
        {
            Background = _Brush("CockpitHairlineBrush", "#2a2f39"),
            ResizeDirection = GridResizeDirection.Columns,
        };

        var pane = new DockPanel { Children = { _Docked(_PathBar(), Dock.Top), _rowScroll } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,3,*") };
        Grid.SetColumn(_tree, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(pane, 2);
        grid.Children.Add(_tree);
        grid.Children.Add(splitter);
        grid.Children.Add(pane);
        return grid;
    }

    private Control _PathBar()
    {
        _pathDirectory.Foreground = _Brush("CockpitTextFaintBrush", "#656c78");
        _pathName.Foreground = _Brush("CockpitTextPrimaryBrush", "#e8eaef");
        _pathKind.Foreground = _Brush("CockpitTextSecondaryBrush", "#949aa5");

        return new Border
        {
            Background = _Brush("CockpitPanelBgBrush", "#1a1d24"),
            Padding = new Thickness(12, 7, 12, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39"),
            Child = new DockPanel
            {
                Children =
                {
                    _Docked(_pathKind, Dock.Right),
                    _pathDirectory,
                    _pathName,
                },
            },
        };
    }

    private async Task _LoadAsync()
    {
        _header.Text = "Reading changes…";
        _totals.Text = string.Empty;
        _tree.ItemsSource = null;
        _rows.Children.Clear();
        _ShowPath(null);
        _review.IsEnabled = false;
        _copy.IsEnabled = false;

        GitDiffResult result;
        try
        {
            result = await _reader.ReadAsync(_session.WorkingDirectory ?? string.Empty, CancellationToken.None);
        }
        catch (Exception)
        {
            result = GitDiffResult.Unavailable;
        }

        _branch = result.Branch;
        _diff = result.Diff;

        if (!result.Available)
        {
            _header.Text = "No git repository here, or git is not available.";
            return;
        }

        if (!result.HasChanges)
        {
            _header.Text = string.IsNullOrEmpty(result.Branch)
                ? "No uncommitted changes."
                : $"No uncommitted changes on '{result.Branch}'.";
            _review.IsEnabled = true; // still lets the operator ask for a review (e.g. of committed work)
            return;
        }

        _header.Text = $"Uncommitted changes on '{result.Branch}' — review before it lands.";
        _review.IsEnabled = true;
        _copy.IsEnabled = true;

        var files = DiffParser.Parse(result.Diff);
        if (files.Count == 0)
        {
            _rows.Children.Add(_Note("Could not read this diff — use Copy diff to see it as text."));
            return;
        }

        _totals.Text = $"{files.Count} {(files.Count == 1 ? "file" : "files")}   +{files.Sum(f => f.Added)}  −{files.Sum(f => f.Removed)}";
        var nodes = FileTree.Build(files);
        _tree.ItemsSource = nodes;
        _ShowFile(files[0]);
    }

    private void _ShowFile(FileDiff file)
    {
        _ShowPath(file);
        _rows.Children.Clear();
        _rowScroll.Offset = default;

        if (file.Kind == FileChangeKind.Binary)
        {
            _rows.Children.Add(_Note("Binary or oversized file — listed, not drawn."));
            return;
        }

        var rows = file.Rows;
        var count = Math.Min(rows.Count, MaxRenderedLines);
        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            if (row.Kind == DiffLineKind.Hunk)
            {
                _rows.Children.Add(_HunkSeparator(row.Text));
                continue;
            }

            // A word-level highlight is only meaningful for a lone removed/added pair, and both halves of that pair
            // need the same span, so each row asks about the pair it belongs to.
            (int Start, int OldEnd, int NewEnd)? span = null;
            if (DiffParser.IsIsolatedReplacement(rows, i))
            {
                span = DiffParser.WordSpan(row.Text, rows[i + 1].Text);
            }
            else if (i > 0 && DiffParser.IsIsolatedReplacement(rows, i - 1))
            {
                span = DiffParser.WordSpan(rows[i - 1].Text, row.Text);
            }

            _rows.Children.Add(_LineRow(row, span));
        }

        if (rows.Count > count)
        {
            _rows.Children.Add(_Note($"… {rows.Count - count} more lines in this file — use Copy diff to see all."));
        }
    }

    private void _ShowPath(FileDiff? file)
    {
        if (file is null)
        {
            _pathDirectory.Text = _pathName.Text = _pathKind.Text = string.Empty;
            return;
        }

        var cut = file.Path.LastIndexOf('/');
        _pathDirectory.Text = cut < 0 ? string.Empty : file.Path[..(cut + 1)];
        _pathName.Text = file.Name;
        _pathKind.Text = file.Kind == FileChangeKind.Binary
            ? "binary"
            : $"{_Describe(file.Kind)}   +{file.Added}  −{file.Removed}";
    }

    private static string _Describe(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "new file",
        FileChangeKind.Deleted => "deleted",
        FileChangeKind.Renamed => "renamed",
        FileChangeKind.Binary => "binary",
        _ => "modified",
    };

    /// <summary>One node of the tree: a folder label, or a file with its status glyph and its <c>+n −m</c>.</summary>
    private Control _NodeRow(TreeNode node)
    {
        if (node.File is null)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.Folder, Width = 13, Height = 13, Foreground = _Brush("CockpitTextFaintBrush", "#656c78") },
                    new TextBlock { Text = node.Label, FontSize = 12, Foreground = _Brush("CockpitTextSecondaryBrush", "#949aa5"), VerticalAlignment = VerticalAlignment.Center },
                },
            };
        }

        var file = node.File;
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        if (file.Added > 0)
        {
            counts.Children.Add(new TextBlock { Text = $"+{file.Added}", FontFamily = Mono, FontSize = 11, Foreground = _Brush("CockpitStatusDoneBrush", "#5AA576") });
        }

        if (file.Removed > 0)
        {
            counts.Children.Add(new TextBlock { Text = $"−{file.Removed}", FontFamily = Mono, FontSize = 11, Foreground = _Brush("CockpitStatusErrorBrush", "#D64545") });
        }

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(_Docked(counts, Dock.Right));
        panel.Children.Add(_Docked(
            new MaterialIcon
            {
                Kind = _Glyph(file.Kind),
                Width = 13,
                Height = 13,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = _GlyphColour(file.Kind),
            },
            Dock.Left));
        panel.Children.Add(new TextBlock
        {
            Text = file.Name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextPrimaryBrush", "#e8eaef"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static MaterialIconKind _Glyph(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => MaterialIconKind.FilePlusOutline,
        FileChangeKind.Deleted => MaterialIconKind.FileRemoveOutline,
        FileChangeKind.Renamed => MaterialIconKind.FileMoveOutline,
        FileChangeKind.Binary => MaterialIconKind.FileQuestionOutline,
        _ => MaterialIconKind.FileEditOutline,
    };

    private static IBrush _GlyphColour(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => _Brush("CockpitStatusDoneBrush", "#5AA576"),
        FileChangeKind.Deleted => _Brush("CockpitStatusErrorBrush", "#D64545"),
        _ => _Brush("CockpitStatusWaitingBrush", "#E0A33E"),
    };

    /// <summary>
    /// A hunk header as a rule with its range and the enclosing declaration beside it. As a line of accent-coloured
    /// text it was just another row to read past; as a separator it does what the <c>@@</c> line is actually for —
    /// saying that the file jumps here.
    /// </summary>
    private Control _HunkSeparator(string header)
    {
        var (range, context) = DiffParser.SplitHunkHeader(header);
        var panel = new DockPanel { Margin = new Thickness(12, 11, 12, 3), LastChildFill = true };
        panel.Children.Add(_Docked(
            new TextBlock { Text = range, FontFamily = Mono, FontSize = 11, Foreground = _Brush("CockpitTextFaintBrush", "#656c78"), VerticalAlignment = VerticalAlignment.Center },
            Dock.Left));

        if (context.Length > 0)
        {
            panel.Children.Add(_Docked(
                new TextBlock
                {
                    Text = context,
                    FontFamily = Mono,
                    FontSize = 11,
                    MaxWidth = 380,
                    Margin = new Thickness(10, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = _Brush("CockpitTextSecondaryBrush", "#949aa5"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Dock.Left));
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = _Brush("CockpitHairlineBrush", "#2a2f39"),
        });
        return panel;
    }

    /// <summary>One diff line: old number, new number, then the code on a band that says what happened to it.</summary>
    private Control _LineRow(DiffRow row, (int Start, int OldEnd, int NewEnd)? span)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{GutterWidth},{GutterWidth},*") };
        grid.Children.Add(_GutterCell(row.OldLine, 0));
        grid.Children.Add(_GutterCell(row.NewLine, 1));

        var code = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 12,
            // ponytail: wrapped rather than horizontally scrollable — a scrollable row cannot also carry a band the
            // full width of the pane without measuring the widest line by hand. Revisit if long lines annoy in use.
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 12, 0),
            Foreground = _Colour(row.Kind),
        };

        var sign = row.Kind switch { DiffLineKind.Added => "+ ", DiffLineKind.Removed => "− ", _ => "  " };
        var text = row.Text.Replace("\t", "    "); // a tab renders as nothing useful in a TextBlock
        if (span is { } s && s.OldEnd > s.Start && row.Kind is DiffLineKind.Added or DiffLineKind.Removed && _CanHighlight(text, row, s))
        {
            var end = row.Kind == DiffLineKind.Added ? s.NewEnd : s.OldEnd;
            code.Inlines?.Add(new Run(sign + text[..s.Start]));
            code.Inlines?.Add(new Run(text[s.Start..end]) { Background = _Tint(row.Kind, strong: true) });
            code.Inlines?.Add(new Run(text[end..]));
        }
        else
        {
            code.Text = sign + text;
        }

        Grid.SetColumn(code, 2);
        grid.Children.Add(code);

        return new Border { Background = _Tint(row.Kind, strong: false), Child = grid };
    }

    /// <summary>
    /// Whether the computed span still addresses this row's text. The span is measured on the raw line but drawn on
    /// the tab-expanded one, so a line with tabs before the change would slice at the wrong offsets.
    /// </summary>
    private static bool _CanHighlight(string text, DiffRow row, (int Start, int OldEnd, int NewEnd) span)
    {
        var end = row.Kind == DiffLineKind.Added ? span.NewEnd : span.OldEnd;
        return text.Length == row.Text.Length && end > span.Start && end <= text.Length;
    }

    private Control _GutterCell(int? number, int column)
    {
        var block = new TextBlock
        {
            Text = number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            FontFamily = Mono,
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = _Brush("CockpitTextFaintBrush", "#656c78"),
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private Control _Note(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(14, 12, 14, 12),
        Foreground = _Brush("CockpitTextFaintBrush", "#656c78"),
    };

    private async Task _ReviewAsync()
    {
        try
        {
            // InjectIntoActiveSessionAsync writes to the selected session, but this panel is about one specific
            // session. Only inject when that session is the selected one — otherwise the review prompt would land in
            // an unrelated session's input. If it is not selected (or none is), say so rather than inject blindly.
            if (!string.Equals(_host.Sessions.ActivePaneId, _session.PaneId, StringComparison.Ordinal))
            {
                _host.ShowToast(
                    "Select this session first, then click Review — the prompt goes to the selected session.",
                    PluginToastSeverity.Warning);
                return;
            }

            await _host.Actions.InjectIntoActiveSessionAsync(ReviewPrompt.Build(_branch));
        }
        catch (Exception)
        {
            // Injecting is a convenience — a failure must not crash the dialog.
        }
    }

    private async Task _CopyAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_diff))
            {
                await _host.Actions.SetClipboardTextAsync(_diff);
            }
        }
        catch (Exception)
        {
            // Copy is best-effort.
        }
    }

    /// <summary>
    /// A diff line's colour, taken from the theme rather than from git's own palette so the panel belongs to the
    /// app it opens in.
    /// </summary>
    private static IBrush _Colour(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => _Brush("CockpitStatusDoneBrush", "#5AA576"),
        DiffLineKind.Removed => _Brush("CockpitStatusErrorBrush", "#D64545"),
        _ => _Brush("CockpitTextSecondaryBrush", "#949aa5"),
    };

    /// <summary>
    /// The band behind a changed line, and the stronger tint behind the part of it that actually differs. Both come
    /// from the status chip/ring tints the theme already defines, so the panel picks up a repaint for free.
    /// </summary>
    private static IBrush? _Tint(DiffLineKind kind, bool strong) => (kind, strong) switch
    {
        (DiffLineKind.Added, false) => _Brush("CockpitStatusDoneChipTintBrush", "#145AA576"),
        (DiffLineKind.Added, true) => _Brush("CockpitStatusDoneRingTintBrush", "#1F5AA576"),
        (DiffLineKind.Removed, false) => _Brush("CockpitStatusErrorChipTintBrush", "#14D64545"),
        (DiffLineKind.Removed, true) => _Brush("CockpitStatusErrorRingTintBrush", "#1FD64545"),
        _ => null,
    };

    /// <summary>
    /// The host's theme brush, resolved at call time. The fallback hex is only reached with no
    /// <see cref="Application"/> (designer, headless test) and is held equal to its token by the repository's theme
    /// guard, so it cannot drift away from the colour it stands in for.
    /// </summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
