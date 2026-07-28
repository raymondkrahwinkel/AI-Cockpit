using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.FanOut;

/// <summary>
/// The whole body of a Fan-out workspace: a short form that takes one task and the arms to run it on, and —
/// once started — the tiles those arms run in. The sessions are real host sessions embedded through
/// <see cref="IWorkspaceContext.EmbedSession"/>, so the host owns their lifetime and keeps them out of the
/// session grid; closing the workspace ends all of them. This surface only decides what to ask for and where
/// to put what comes back.
/// </summary>
internal sealed class FanOutWorkspaceBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly IWorkspaceContext _context;
    private readonly List<FanOutVariantEditor> _variants = [];
    private readonly StackPanel _variantList = new() { Spacing = 6 };
    private readonly TextBox _task;
    private readonly TextBox _workingDirectory;
    private readonly Button _addVariant;
    private readonly Button _start;

    private IReadOnlyList<string> _profiles = [];
    private bool _started;

    public FanOutWorkspaceBody(ICockpitHost host, IWorkspaceContext context)
    {
        _host = host;
        _context = context;

        _task = new TextBox
        {
            PlaceholderText = "The one task every arm gets",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
        };
        _task.TextChanged += (_, _) => _RefreshControls();

        _workingDirectory = new TextBox
        {
            PlaceholderText = "Repository to work in — each arm gets its own worktree of it",
            Text = _context.Sessions.ActiveSessionWorkingDirectory ?? string.Empty,
        };

        _addVariant = new Button { Content = "Add arm" };
        _addVariant.Click += (_, _) => _AddVariant();

        _start = new Button { Content = "Start", IsEnabled = false };
        _start.Click += (_, _) => _Start();

        Content = _BuildSetup();

        for (var index = 0; index < FanOutRun.MinimumVariants; index++)
        {
            _AddVariant();
        }

        _ = _LoadProfilesAsync();
    }

    private Control _BuildSetup()
    {
        var intro = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "Fan-out", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock
                {
                    Text = "One task, several agents on it at once — each in its own worktree, so their work never collides. "
                           + "Vary the profile to put different providers on the same brief, vary the angle to get different takes from one.",
                    Opacity = 0.7,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _addVariant, _start },
        };

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(20),
                MaxWidth = 780,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    intro,
                    _Labelled("Task", _task),
                    _Labelled("Working directory", _workingDirectory),
                    _Labelled($"Arms ({FanOutRun.MinimumVariants}–{FanOutRun.MaximumVariants})", _variantList),
                    buttons,
                },
            },
        };
    }

    private void _AddVariant()
    {
        if (_variants.Count >= FanOutRun.MaximumVariants)
        {
            return;
        }

        var editor = new FanOutVariantEditor(_Placeholder(_variants.Count));
        editor.RemoveRequested += (_, _) => _RemoveVariant(editor);
        editor.ShowProfiles(_profiles, _variants.Count);

        _variants.Add(editor);
        _variantList.Children.Add(editor.View);
        _RefreshControls();
    }

    private void _RemoveVariant(FanOutVariantEditor editor)
    {
        if (_variants.Count <= FanOutRun.MinimumVariants)
        {
            return;
        }

        _variants.Remove(editor);
        _variantList.Children.Remove(editor.View);
        _RefreshControls();
    }

    private void _RefreshControls()
    {
        _addVariant.IsEnabled = _variants.Count < FanOutRun.MaximumVariants;
        _start.IsEnabled = _ReadRun().CanStart;
    }

    private FanOutRun _ReadRun() =>
        new(_task.Text ?? string.Empty,
            _workingDirectory.Text ?? string.Empty,
            _variants.Select(variant => variant.ToVariant()).ToList());

    private async Task _LoadProfilesAsync()
    {
        var profiles = await _host.GetProfilesAsync();
        _profiles = profiles.Select(profile => profile.Label).ToList();

        for (var index = 0; index < _variants.Count; index++)
        {
            _variants[index].ShowProfiles(_profiles, index);
        }
    }

    private void _Start() => Start(_ReadRun());

    /// <summary>
    /// Starts a run: one session per arm, laid out on the tile grid. Split from reading the form so that what a
    /// run asks the host for, and where its tiles land, is settled in one place — and can be observed without
    /// driving the form.
    /// </summary>
    /// <remarks>
    /// A workspace starts one run and keeps it: a second call would embed another full set of sessions and then
    /// replace the tiles holding the first set, leaving those sessions running with nothing on screen to reach or
    /// stop them — real agents in real worktrees, spending, invisibly. Refusing here rather than relying on the
    /// button being off-screen keeps that true whoever calls, which is the point of it being the seam.
    /// </remarks>
    internal void Start(FanOutRun run)
    {
        if (_started || !run.CanStart)
        {
            return;
        }

        _started = true;
        var requests = run.ToRequests(Guid.NewGuid().ToString("n"));
        var layout = FanOutTileLayout.For(requests.Count);

        var tiles = new Grid
        {
            Margin = new Thickness(12),
            ColumnDefinitions = ColumnDefinitions.Parse(_Stars(layout.Columns)),
            RowDefinitions = RowDefinitions.Parse(_Stars(layout.Rows)),
        };

        for (var index = 0; index < requests.Count; index++)
        {
            var placement = layout.Tiles[index];
            var tile = _Tile(run.Variants[index], _context.EmbedSession(requests[index]).View);

            Grid.SetColumn(tile, placement.Column);
            Grid.SetRow(tile, placement.Row);
            Grid.SetColumnSpan(tile, placement.ColumnSpan);
            tiles.Children.Add(tile);
        }

        Content = new DockPanel { LastChildFill = true, Children = { _RunHeader(run), tiles } };
    }

    private Control _RunHeader(FanOutRun run) =>
        new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = FanOutBrief.Label(run.Task), FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock
                    {
                        Text = $"{run.Variants.Count} arms, each in its own worktree. Closing this workspace ends all of them.",
                        Opacity = 0.7,
                        FontSize = 12,
                    },
                },
            },
        };

    private Control _Tile(FanOutVariant variant, Control session) =>
        new Border
        {
            Margin = new Thickness(4),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39"),
            CornerRadius = new CornerRadius(4),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new TextBlock
                    {
                        Text = variant.Describe(),
                        Margin = new Thickness(10, 7),
                        FontSize = 12,
                        Opacity = 0.75,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        [DockPanel.DockProperty] = Dock.Top,
                    },
                    session,
                },
            },
        };

    private static Control _Labelled(string label, Control field) =>
        new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
                field,
            },
        };

    /// <summary>
    /// The first two rows say what an angle is for by example — a fan-out set up without one runs the same brief
    /// several times, which is the one way it cannot pay off.
    /// </summary>
    private static string _Placeholder(int index) => index switch
    {
        0 => "Angle — e.g. the smallest change that works",
        1 => "Angle — e.g. the version you would still want in a year",
        _ => "Angle (optional)",
    };

    private static string _Stars(int count) => string.Join(',', Enumerable.Repeat("*", count));

    /// <summary>The host's theme brush, resolved at call time; the fallback hex is only reached with no application (designer, headless test).</summary>
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
