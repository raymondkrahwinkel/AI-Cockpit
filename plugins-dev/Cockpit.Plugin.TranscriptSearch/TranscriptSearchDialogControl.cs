using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.TranscriptSearch;

/// <summary>
/// The "Search transcripts" dialog: a query box over the on-disk <c>claude</c> transcripts, showing the matching
/// user/assistant lines with a snippet, which session and project they came from and when. Each hit can have its
/// session id copied (to resume it with <c>claude --resume</c>) or its transcript revealed in the file explorer.
/// Search runs on Enter or the button rather than as-you-type, so a broad history is not re-scanned on every
/// keystroke. Built in code; the colours come from the host's theme resources.
/// </summary>
internal sealed class TranscriptSearchDialogControl : UserControl
{
    private const int MinQueryLength = 2;

    private readonly TranscriptSearchService _search;
    private readonly ICockpitActions _actions;

    private readonly TextBox _query;
    private readonly Button _searchButton;
    private readonly TextBlock _status;
    private readonly ObservableCollection<TranscriptSearchHit> _results = [];

    public TranscriptSearchDialogControl(TranscriptSearchService search, ICockpitActions actions)
    {
        _search = search;
        _actions = actions;

        _query = new TextBox
        {
            PlaceholderText = "Search across all your past sessions…",
            Width = 480,
        };
        _query.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                await _SearchAsync();
            }
        };

        _searchButton = new Button { Content = "Search", Classes = { "Accent" } };
        _searchButton.Click += async (_, _) => await _SearchAsync();

        _status = new TextBlock
        {
            Text = "Search your past sessions by any text you or the agent wrote.",
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var searchBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { _query, _searchButton },
        };

        var hits = new ItemsControl
        {
            ItemsSource = _results,
            ItemTemplate = new FuncDataTemplate<TranscriptSearchHit>((hit, _) => _HitCard(hit), supportsRecycling: true),
        };

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(searchBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(searchBar);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = hits });
        Content = root;

        Loaded += (_, _) => _query.Focus();
    }

    private Control _HitCard(TranscriptSearchHit hit)
    {
        var rolePill = new Border
        {
            Background = _Brush("CockpitPanelBgBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            Child = new TextBlock { Text = hit.Role, FontSize = 10, Foreground = _Brush("CockpitAccentBrush") },
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                rolePill,
                new TextBlock
                {
                    Text = hit.Project,
                    FontSize = 11,
                    Opacity = 0.7,
                    MaxWidth = 360,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = hit.ModifiedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
                    FontSize = 10,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var copyId = new Button { Content = "Copy id", FontSize = 11, Padding = new Thickness(8, 2) };
        copyId.Click += async (_, _) =>
        {
            await _actions.SetClipboardTextAsync(hit.SessionId);
            _status.Text = $"✓ Session id copied — resume it with 'claude --resume {hit.SessionId}'.";
        };

        var reveal = new Button { Content = "Reveal", FontSize = 11, Padding = new Thickness(8, 2) };
        reveal.Click += (_, _) => _Reveal(hit.FilePath);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = hit.SessionId,
                    FontFamily = _MonoFont(),
                    FontSize = 10,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                copyId,
                reveal,
            },
        };

        return new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    header,
                    new SelectableTextBlock { Text = hit.Snippet, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                    footer,
                },
            },
        };
    }

    private async Task _SearchAsync()
    {
        var query = _query.Text?.Trim() ?? string.Empty;
        _results.Clear();

        if (query.Length < MinQueryLength)
        {
            _status.Text = $"Type at least {MinQueryLength} characters to search.";
            return;
        }

        _searchButton.IsEnabled = false;
        _status.Text = "Searching…";
        try
        {
            var hits = await _search.SearchAsync(query);
            foreach (var hit in hits)
            {
                _results.Add(hit);
            }

            _status.Text = hits.Count switch
            {
                0 => "No matches.",
                1 => "1 match.",
                _ => $"{hits.Count} matches (most recent sessions first).",
            };
        }
        catch (Exception exception)
        {
            _status.Text = $"Search failed: {exception.Message}";
        }
        finally
        {
            _searchButton.IsEnabled = true;
        }
    }

    // Opens the transcript's containing folder in the OS file explorer. Best-effort — a failure to launch it is
    // reported in the status line rather than thrown.
    private void _Reveal(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _status.Text = "That transcript is no longer on disk.";
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = true });
            }
            else
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Process.Start(new ProcessStartInfo("xdg-open", folder) { UseShellExecute = true });
                }
            }
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not open the folder: {exception.Message}";
        }
    }

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : new FontFamily("Cascadia Mono, Consolas, monospace");

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
