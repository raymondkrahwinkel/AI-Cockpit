using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    // The placeholder tokens a template body may carry (AC-189), shown as the placeholder-help under the Templates
    // section and offered as quick-insert chips in the editor. Kept here so the help and the editor stay in lockstep with
    // what AutopilotTemplateResolver actually fills.
    private static readonly string[] _Placeholders =
    [
        "{{issue.id}}",
        "{{issue.title}}",
        "{{issue.description}}",
        "{{issue.url}}",
        "{{issue.tracker}}",
        "{{input.<naam>}}",
    ];

    private readonly AutopilotSettings _settings;
    private readonly ICockpitHost _host;
    private readonly AutopilotTemplateStore _templates;
    private readonly ComboBox _ceoProfile;
    private readonly AutoCompleteBox _ceoModel;
    private readonly ComboBox _costStrategy;
    private readonly NumericUpDown _maxAttempts;
    private readonly NumericUpDown _maxConcurrent;
    private readonly ComboBox _autonomy;
    private readonly StackPanel _templateList = new() { Spacing = 0 };

    // The loaded profiles, so selecting one can look up its provider to decide which model suggestions to offer.
    private IReadOnlyList<PluginProfileInfo> _profiles = [];
    private bool _profilesLoaded;

    public AutopilotSettingsControl(AutopilotSettings settings, ICockpitHost host, AutopilotTemplateStore templates)
    {
        _settings = settings;
        _host = host;
        _templates = templates;

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
        _maxConcurrent = _Number(settings.MaxConcurrentRuns(), min: 1, max: 8);
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
        panel.Children.Add(_Row("Runs at once (rest queue up)", _maxConcurrent));
        panel.Children.Add(_Row("Autonomy (permission mode)", _autonomy));
        panel.Children.Add(_Hint("How autonomous a run is on the CLI side; the host still gates shell and egress. bypassPermissions = works without asking before edits."));

        panel.Children.Add(_Header("Templates"));
        panel.Children.Add(_Hint("Goal/brief templates you can start a run from in the plan flow. Builtin and plugin templates you can edit (kept as an override) and reset to their default; your own you can also delete."));
        var newTemplate = new Button
        {
            Content = "+ New template",
            Padding = new Thickness(11, 5),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        newTemplate.Click += (_, _) => _EditTemplate(null);
        panel.Children.Add(newTemplate);
        panel.Children.Add(_templateList);
        panel.Children.Add(_Hint($"Placeholders you can use in a body (filled from the triggering issue and your input): {string.Join("  ", _Placeholders)}"));

        // Re-render the list whenever a template changes (created, edited, deleted, reset) so the section stays in step
        // with the store, the same way the plan surface tracks its queue/history.
        _templates.Changed += _OnTemplatesChanged;
        DetachedFromVisualTree += (_, _) => _templates.Changed -= _OnTemplatesChanged;
        _RenderTemplates();

        Content = panel;
    }

    private void _OnTemplatesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _RenderTemplates();
        }
        else
        {
            Dispatcher.UIThread.Post(_RenderTemplates);
        }
    }

    // Rebuilds the template rows from the combined list — the plugin/builtin registrations with any override applied,
    // then the operator's own — each with its name, an origin badge, and the actions its origin allows.
    private void _RenderTemplates()
    {
        _templateList.Children.Clear();

        IReadOnlyList<AutopilotTemplate> templates;
        try
        {
            templates = _templates.List(_host.RegisteredAutopilotTemplates);
        }
        catch (Exception)
        {
            templates = [];
        }

        if (templates.Count == 0)
        {
            _templateList.Children.Add(new TextBlock
            {
                Text = "No templates yet — create one, or a plugin (YouTrack, GitHub) contributes its own.",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitTextFaintBrush"),
            });
            return;
        }

        foreach (var template in templates)
        {
            _templateList.Children.Add(_TemplateRow(template));
        }
    }

    private Control _TemplateRow(AutopilotTemplate template)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, [DockPanel.DockProperty] = Dock.Right };

        var edit = new Button { Content = "Edit", Padding = new Thickness(9, 3), FontSize = 11 };
        edit.Click += (_, _) => _EditTemplate(template);
        actions.Children.Add(edit);

        // A plugin/builtin template can be reset to its registered default (dropping the override); a user template has no
        // registration to reset to, so it gets a delete instead.
        if (template.Deletable)
        {
            var delete = new Button { Content = "Delete", Padding = new Thickness(9, 3), FontSize = 11 };
            delete.Click += (_, _) => _templates.DeleteUserTemplate(template.Id);
            actions.Children.Add(delete);
        }
        else
        {
            var reset = new Button
            {
                Content = "Reset to default",
                Padding = new Thickness(9, 3),
                FontSize = 11,
                [ToolTip.TipProperty] = "Drop your edit and show the registered template again.",
            };
            reset.Click += (_, _) => _templates.ResetOverride(template.Id);
            actions.Children.Add(reset);
        }

        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _OriginBadge(template.Origin),
                new TextBlock
                {
                    Text = template.Name,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = _Brush("CockpitTextPrimaryBrush"),
                },
            },
        };

        return new Border
        {
            Padding = new Thickness(0, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel { LastChildFill = true, Children = { actions, name } },
        };
    }

    private static Control _OriginBadge(AutopilotTemplateOrigin origin)
    {
        var (text, key) = origin switch
        {
            AutopilotTemplateOrigin.Builtin => ("Builtin", "CockpitTextSecondaryBrush"),
            AutopilotTemplateOrigin.Plugin => ("Plugin", "CockpitAccentBrush"),
            _ => ("User", "CockpitStatusDoneBrush"),
        };

        return new Border
        {
            Background = _Brush("CockpitPanelBgBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = _Brush(key) },
        };
    }

    // Opens the create/edit dialog for a template. A null template creates a fresh user template; a plugin/builtin
    // template is edited into an override; a user template edits in place. The id keeps a user template stable across an
    // edit (so it is not duplicated); a new user template gets a generated one, and a plugin/builtin edit reuses the
    // registration's id so the override keys to it.
    private void _EditTemplate(AutopilotTemplate? template)
    {
        var isNew = template is null;
        var origin = template?.Origin ?? AutopilotTemplateOrigin.User;

        var nameBox = new TextBox
        {
            Text = template?.Name ?? string.Empty,
            PlaceholderText = "Template name",
            FontSize = 12,
        };
        var bodyBox = new TextBox
        {
            Text = template?.Body ?? string.Empty,
            PlaceholderText = "Brief text — use placeholders like {{issue.title}} or {{input.branch}}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            FontSize = 12,
        };

        _ = _host.ShowDialogAsync(isNew ? "New template" : $"Edit “{template!.Name}”", () =>
        {
            var save = new Button
            {
                Content = "Save",
                Padding = new Thickness(15, 8),
                CornerRadius = new CornerRadius(7),
                FontWeight = FontWeight.SemiBold,
                Background = _Brush("CockpitAccentBrush"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E)),
                HorizontalAlignment = HorizontalAlignment.Right,
                [DockPanel.DockProperty] = Dock.Right,
            };
            save.Click += (sender, _) =>
            {
                var name = (nameBox.Text ?? string.Empty).Trim();
                var body = bodyBox.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _StoreTemplate(template, origin, name, body);
                    (sender as Control)?.FindAncestorOfType<Window>()?.Close();
                }
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(13, 8),
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                [DockPanel.DockProperty] = Dock.Right,
            };
            cancel.Click += (sender, _) => (sender as Control)?.FindAncestorOfType<Window>()?.Close();

            var footer = new Border
            {
                Padding = new Thickness(14, 11),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                [DockPanel.DockProperty] = Dock.Bottom,
                Child = new DockPanel { LastChildFill = false, Children = { save, cancel } },
            };

            // Quick-insert chips for the placeholders, so the operator does not have to remember the exact tokens: a click
            // appends the token at the caret's end of the body.
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var placeholder in _Placeholders)
            {
                var chip = new Button { Content = placeholder, Padding = new Thickness(7, 2), FontSize = 10.5, Margin = new Thickness(0, 0, 4, 4) };
                chip.Click += (_, _) => bodyBox.Text = (bodyBox.Text ?? string.Empty) + placeholder;
                chips.Children.Add(chip);
            }

            var body = new StackPanel
            {
                Margin = new Thickness(16, 14),
                Spacing = 8,
                Children =
                {
                    origin != AutopilotTemplateOrigin.User
                        ? _Hint("Editing a plugin/builtin template is kept as your override; use Reset to default to drop it.")
                        : new Panel(),
                    new TextBlock { Text = "Name", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitTextSecondaryBrush") },
                    nameBox,
                    new TextBlock { Text = "Body", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitTextSecondaryBrush") },
                    bodyBox,
                    new TextBlock { Text = "Insert a placeholder:", FontSize = 10.5, Foreground = _Brush("CockpitTextFaintBrush") },
                    chips,
                },
            };

            return new DockPanel { LastChildFill = true, Children = { footer, new ScrollViewer { Content = body } } };
        }, 620, 620);
    }

    // Persists an edit: a user template (new or existing) is upserted directly; a plugin/builtin edit is recorded as an
    // override keyed to the registration's id, so its default can be restored by dropping the override.
    private void _StoreTemplate(AutopilotTemplate? template, AutopilotTemplateOrigin origin, string name, string body)
    {
        if (origin == AutopilotTemplateOrigin.User)
        {
            var id = template?.Id ?? $"user.{Guid.NewGuid():N}";
            _templates.UpsertUserTemplate(AutopilotTemplate.ForUser(id, name, body, template?.RequiredPlaceholders));
        }
        else
        {
            _templates.UpsertOverride(new AutopilotTemplateOverride(template!.Id, name, body, template.RequiredPlaceholders));
        }
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
        _settings.SetMaxConcurrentRuns((int)(_maxConcurrent.Value ?? 1));
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
