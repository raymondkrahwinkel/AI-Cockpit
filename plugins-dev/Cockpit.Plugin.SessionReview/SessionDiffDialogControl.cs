using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.SessionReview;

/// <summary>
/// The per-session review panel (AC-50): shows the uncommitted <c>git diff</c> of the session's working directory,
/// coloured for reading, with one click to ask the session to review its own changes or to copy the diff. Read-only
/// and operator-triggered — no consent gate.
/// </summary>
internal sealed class SessionDiffDialogControl : UserControl
{
    // A large diff is for scanning, not for rendering ten thousand text blocks; cap what is drawn and say so.
    private const int MaxRenderedLines = 2000;

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,DejaVu Sans Mono,monospace");

    private readonly ICockpitActions _actions;
    private readonly string? _workingDirectory;
    private readonly GitDiffReader _reader = new();
    private readonly TextBlock _header = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _lines = new() { Spacing = 0 };
    private readonly Button _review;
    private readonly Button _copy;

    private string _diff = string.Empty;
    private string _branch = string.Empty;

    public SessionDiffDialogControl(ICockpitActions actions, string? workingDirectory)
    {
        _actions = actions;
        _workingDirectory = workingDirectory;

        _review = new Button { Content = "Ask this session to review", IsEnabled = false };
        _review.Click += async (_, _) => await _ReviewAsync();

        _copy = new Button { Content = "Copy diff", IsEnabled = false };
        _copy.Click += async (_, _) => await _CopyAsync();

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => _ = _LoadAsync();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _review, _copy, refresh },
        };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);

        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                _header,
                buttons,
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = _lines,
                },
            },
        };

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        _header.Text = "Reading changes…";
        _lines.Children.Clear();
        _review.IsEnabled = false;
        _copy.IsEnabled = false;

        GitDiffResult result;
        try
        {
            result = await _reader.ReadAsync(_workingDirectory ?? string.Empty, CancellationToken.None);
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
        _RenderDiff(result.Diff);
    }

    private void _RenderDiff(string diff)
    {
        var all = diff.Replace("\r\n", "\n").Split('\n');
        var count = Math.Min(all.Length, MaxRenderedLines);
        for (var i = 0; i < count; i++)
        {
            var line = all[i];
            _lines.Children.Add(new TextBlock
            {
                Text = line.Length == 0 ? " " : line,
                FontFamily = Mono,
                FontSize = 12,
                Foreground = _Colour(GitDiffReader.ClassifyLine(line)),
            });
        }

        if (all.Length > MaxRenderedLines)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = $"… {all.Length - MaxRenderedLines} more lines — use Copy diff to see all.",
                FontFamily = Mono,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = _Brush("CockpitTextFaintBrush", Color.Parse("#9AA0A6")),
            });
        }
    }

    private async Task _ReviewAsync()
    {
        await _actions.InjectIntoActiveSessionAsync(ReviewPrompt.Build(_branch));
    }

    private async Task _CopyAsync()
    {
        if (!string.IsNullOrEmpty(_diff))
        {
            await _actions.SetClipboardTextAsync(_diff);
        }
    }

    private static IBrush _Colour(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => _Brush("CockpitStatusDoneBrush", Color.Parse("#6BBF59")),
        DiffLineKind.Removed => _Brush("CockpitStatusErrorBrush", Color.Parse("#D9534F")),
        DiffLineKind.Hunk => _Brush("CockpitAccentBrush", Color.Parse("#5A9BD4")),
        DiffLineKind.FileHeader => _Brush("CockpitTextBrush", Color.Parse("#D0D0D0")),
        _ => _Brush("CockpitTextFaintBrush", Color.Parse("#9AA0A6")),
    };

    private static IBrush _Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
