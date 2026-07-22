using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The settings view (opened from the plugin's gear): kept to the minimum an operator actually fixes (Raymond
/// 2026-07-21) — the CEO's identity and the safety caps a run must not exceed. Everything a run's shape needs — which
/// steps, which profile/model per step, which gates are hard, which tracker stage a phase maps to — is context- or
/// tracker-specific and the CEO decides it dynamically per plan (a global tracker mapping breaks the moment there are
/// two trackers, or a non-tracker workload), so none of that is fixed here. Implements <see cref="IPluginSettingsView"/>
/// so the host dialog shows a Save button; <see cref="Save"/> writes the global level.
/// </summary>
internal sealed class AutopilotSettingsControl : UserControl, IPluginSettingsView
{
    private readonly AutopilotSettings _settings;
    private readonly ICockpitHost _host;
    private readonly ComboBox _ceoProfile;
    private readonly AutoCompleteBox _ceoModel;
    private readonly ComboBox _costStrategy;
    private readonly NumericUpDown _maxAttempts;
    private readonly ComboBox _autonomy;

    // The loaded profiles, so selecting one can look up its provider to decide which model suggestions to offer.
    private IReadOnlyList<PluginProfileInfo> _profiles = [];
    private bool _profilesLoaded;

    public AutopilotSettingsControl(AutopilotSettings settings, ICockpitHost host)
    {
        _settings = settings;
        _host = host;

        _ceoProfile = new ComboBox
        {
            Width = 320,
            PlaceholderText = "Loading profiles…",
        };
        _ceoProfile.SelectionChanged += (_, _) => _OnProfileChanged();

        _ceoModel = new AutoCompleteBox
        {
            Width = 320,
            PlaceholderText = "Model — e.g. opus (blank = profile default)",
            Text = settings.CeoModel() ?? string.Empty,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 0,
            // Off until a real profile is chosen: the model only means something once the CEO runs on a profile that
            // offers a choice, so the field stays inert until then (Raymond 2026-07-21).
            IsEnabled = false,
        };

        // Items are in AutopilotCostStrategy declaration order (CostFirst, Balanced, QualityFirst), so SelectedIndex maps
        // straight to the enum value.
        _costStrategy = new ComboBox
        {
            Width = 340,
            ItemsSource = new[]
            {
                "Cost first — cheapest, local wherever it can work",
                "Balanced — default local/free, paid only when needed",
                "Quality first — the most capable model each step warrants",
            },
            SelectedIndex = (int)settings.CostStrategy(),
        };

        _maxAttempts = _Number(settings.MaxSelfFixAttempts(), min: 0, max: 10);
        _autonomy = new ComboBox
        {
            Width = 220,
            ItemsSource = new[] { "bypassPermissions", "acceptEdits", "default" },
            SelectedItem = settings.AutonomyMode(),
        };

        var panel = new StackPanel { Margin = new Thickness(4), Spacing = 10 };

        panel.Children.Add(_Header("CEO (planning)"));
        panel.Children.Add(_Hint("The profile and model the CEO plans the work with. A strong reasoning model (Opus) is recommended. Blank model uses the profile's own default."));
        panel.Children.Add(_Row("CEO profile", _ceoProfile));
        panel.Children.Add(_Row("CEO model", _ceoModel));

        panel.Children.Add(_Header("Cost & tokens"));
        panel.Children.Add(_Hint("How hard the CEO leans on cost when it picks a model per step. Balanced is the recommended default; the CEO always fits the model to the work, this only moves where the line between a local free model and a paid one sits."));
        panel.Children.Add(_Row("Cost strategy", _costStrategy));

        panel.Children.Add(_Header("Run safety"));
        panel.Children.Add(_Hint("Caps the operator keeps regardless of what the CEO plans."));
        panel.Children.Add(_Row("Max rework attempts per step", _maxAttempts));
        panel.Children.Add(_Row("Autonomy (permission mode)", _autonomy));
        panel.Children.Add(_Hint("How autonomous a run is on the CLI side; the host still gates shell and egress. bypassPermissions = works without asking before edits."));

        Content = panel;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_profilesLoaded)
        {
            _ = _LoadProfilesAsync();
        }
    }

    // Load the cockpit's profiles and fill the picker with the real profiles by label — no "app default" sentinel, since
    // there is no such setting: the CEO runs on a concrete profile. Selects the one the settings hold, else the first.
    // Async because the host reads them off disk; marshalled back to the UI thread.
    private async Task _LoadProfilesAsync()
    {
        _profilesLoaded = true;
        var profiles = await _host.GetProfilesAsync();
        Dispatcher.UIThread.Post(() =>
        {
            _profiles = profiles;
            var labels = profiles.Select(profile => profile.Label).ToList();
            _ceoProfile.ItemsSource = labels;
            _ceoProfile.PlaceholderText = labels.Count == 0 ? "No profiles configured" : null;

            var saved = _settings.CeoProfileLabel();
            _ceoProfile.SelectedItem = !string.IsNullOrWhiteSpace(saved) && labels.Contains(saved)
                ? saved
                : labels.FirstOrDefault();
        });
    }

    // A profile choice decides the model field: enabled once a profile is chosen, and offered the model suggestions the
    // host says that profile has (Claude's aliases; a local or plugin profile pins its own, so the list is empty).
    private void _OnProfileChanged()
    {
        var label = _ceoProfile.SelectedItem as string;
        var hasProfile = !string.IsNullOrEmpty(label);
        _ceoModel.IsEnabled = hasProfile;

        if (!hasProfile)
        {
            _ceoModel.Text = string.Empty;
            _ceoModel.ItemsSource = null;
            return;
        }

        var suggestions = _profiles.FirstOrDefault(profile => profile.Label == label)?.ModelSuggestions;
        _ceoModel.ItemsSource = suggestions is { Count: > 0 } ? suggestions : null;
    }

    public bool Save()
    {
        _settings.SetCeoProfileLabel(_ceoProfile.SelectedItem as string);
        _settings.SetCeoModel(_ceoModel.IsEnabled ? _Trimmed(_ceoModel.Text) : null);
        _settings.SetCostStrategy(_costStrategy.SelectedIndex >= 0 ? (AutopilotCostStrategy)_costStrategy.SelectedIndex : AutopilotCostStrategy.Balanced);
        _settings.SetMaxSelfFixAttempts((int)(_maxAttempts.Value ?? 2));
        _settings.SetAutonomyMode(_autonomy.SelectedItem as string ?? AutopilotSettings.DefaultAutonomyMode);
        return true;
    }

    private static string? _Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static NumericUpDown _Number(int value, int min, int max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Width = 120,
    };

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
        // Left-align every input against the label column: ComboBox and AutoCompleteBox default to different alignments
        // inside a filled DockPanel cell, which left the profile dropdown and the model box starting at different x.
        input.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(text);
        row.Children.Add(input);
        return row;
    }

    private static TextBlock _Header(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) =>
        new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextFaintBrush") };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
