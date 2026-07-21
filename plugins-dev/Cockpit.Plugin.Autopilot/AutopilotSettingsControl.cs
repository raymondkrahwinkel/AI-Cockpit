using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The settings view (opened from the plugin manager's gear): the global level of Autopilot's run behaviour and
/// its per-gate hard/skip choices. Implements <see cref="IPluginSettingsView"/> so the host dialog shows a Save
/// button; <see cref="Save"/> writes the global level (per-project overrides are set from a run's own project
/// context, in a later sub-ticket).
/// </summary>
internal sealed class AutopilotSettingsControl : UserControl, IPluginSettingsView
{
    private readonly AutopilotSettings _settings;
    private readonly NumericUpDown _grace;
    private readonly NumericUpDown _maxAttempts;
    private readonly TextBox _profile;
    private readonly TextBox _scopingProfile;
    private readonly ComboBox _autonomy;
    private readonly ComboBox _comments;
    private readonly Dictionary<GateKind, ComboBox> _gates = [];

    public AutopilotSettingsControl(AutopilotSettings settings)
    {
        _settings = settings;

        _grace = _Number(settings.GraceTimerMinutes(), min: 1, max: 120);
        _maxAttempts = _Number(settings.MaxSelfFixAttempts(), min: 0, max: 10);
        _profile = new TextBox
        {
            PlaceholderText = "Session profile a run starts on (blank = ask each time)",
            Text = settings.DefaultProfileLabel() ?? string.Empty,
            Width = 320,
        };
        _scopingProfile = new TextBox
        {
            PlaceholderText = "Profile the pre-start scoping judgment runs on (blank = no scoping)",
            Text = settings.ScopingProfileLabel() ?? string.Empty,
            Width = 320,
        };
        _autonomy = new ComboBox
        {
            Width = 220,
            ItemsSource = new[] { "bypassPermissions", "acceptEdits", "default" },
            SelectedItem = settings.AutonomyMode(),
        };
        _comments = _Enum(new[] { CommentLevel.QuestionsAndMilestones, CommentLevel.Full }, settings.CommentMirroring());

        var panel = new StackPanel { Margin = new Thickness(4), Spacing = 10 };
        panel.Children.Add(_Header("Run behaviour"));
        panel.Children.Add(_Row("Grace timer (minutes)", _grace));
        panel.Children.Add(_Row("Max self-fix attempts per gate", _maxAttempts));
        panel.Children.Add(_Row("Default session profile", _profile));
        panel.Children.Add(_Row("Scoping profile", _scopingProfile));
        panel.Children.Add(_Row("Autonomy (permission mode)", _autonomy));
        panel.Children.Add(_Hint("How autonomous a run is on the CLI side; the host still gates shell and egress. bypassPermissions = works without asking before edits."));
        panel.Children.Add(_Row("Comment mirroring", _comments));

        panel.Children.Add(_Header("Done-gates"));
        panel.Children.Add(_Hint("Each gate is required (Hard) or advisory (Skip). Security is Hard by default."));
        foreach (var kind in new[] { GateKind.Verify, GateKind.CodeReview, GateKind.Security, GateKind.Conventions })
        {
            var box = _Enum(new[] { GateMode.Hard, GateMode.Skip }, settings.Gate(kind));
            _gates[kind] = box;
            panel.Children.Add(_Row(_GateLabel(kind), box));
        }

        Content = panel;
    }

    public bool Save()
    {
        _settings.SetGraceTimerMinutes((int)(_grace.Value ?? 5));
        _settings.SetMaxSelfFixAttempts((int)(_maxAttempts.Value ?? 2));
        _settings.SetDefaultProfileLabel(string.IsNullOrWhiteSpace(_profile.Text) ? null : _profile.Text.Trim());
        _settings.SetScopingProfileLabel(string.IsNullOrWhiteSpace(_scopingProfile.Text) ? null : _scopingProfile.Text.Trim());
        _settings.SetAutonomyMode(_autonomy.SelectedItem as string ?? AutopilotSettings.DefaultAutonomyMode);
        _settings.SetCommentMirroring(_comments.SelectedItem is CommentLevel level ? level : CommentLevel.QuestionsAndMilestones);
        foreach (var (kind, box) in _gates)
        {
            _settings.SetGate(kind, box.SelectedItem is GateMode mode ? mode : GateMode.Skip);
        }

        return true;
    }

    private static NumericUpDown _Number(int value, int min, int max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Width = 120,
    };

    private static ComboBox _Enum<T>(IReadOnlyList<T> values, T selected) where T : struct, Enum =>
        new() { Width = 220, ItemsSource = values, SelectedItem = selected };

    private static Control _Row(string label, Control input)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var text = new TextBlock
        {
            Text = label,
            Width = 240,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(input);
        return row;
    }

    private static TextBlock _Header(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) =>
        new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextFaintBrush") };

    private static string _GateLabel(GateKind kind) => kind switch
    {
        GateKind.Verify => "Visual verify",
        GateKind.CodeReview => "Code review",
        GateKind.Security => "Security review",
        GateKind.Conventions => "Conventions check",
        _ => kind.ToString(),
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
