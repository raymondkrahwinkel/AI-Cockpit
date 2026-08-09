using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Autopilot;

// The settings view (opened from the plugin's gear): kept to the minimum an operator actually fixes — the
// CEO's identity and the safety caps a run must not exceed. Everything a run's shape needs — which
// steps, which profile/model per step, which gates are hard, which tracker stage a phase maps to — is context- or
// tracker-specific and the CEO decides it dynamically per plan (a global tracker mapping breaks the moment there are
// two trackers, or a non-tracker workload), so none of that is fixed here. Implements `IPluginSettingsView`
// so the host dialog shows a Save button; `Save` writes the global level.
//
// The four groups these settings were already written in (AC-316) are also the dialog's pages: implementing
// `IPluginSettingsSections` puts them in the host's navigation rail instead of stacking them into one
// scroll several screens long. Nothing moved or was renamed — each group is the page it always was.
internal sealed class AutopilotSettingsControl : UserControl, IPluginSettingsView, IPluginSettingsSections
{
    // The placeholder tokens a template body may carry (AC-189), offered as quick-insert chips in the editor and
    // named (with what each one fills in, AC-521) in the placeholder-help under the Templates section — that help
    // spells its own meaning per token rather than joining this array, but names the same tokens; a mismatch is
    // pinned by AutopilotSettingsControlPlaceholderHelpTests against what AutopilotTemplateResolver actually fills.
    private static readonly string[] _Placeholders =
    [
        "{{issue.id}}",
        "{{issue.title}}",
        "{{issue.description}}",
        "{{issue.url}}",
        "{{issue.tracker}}",
        "{{input.<name>}}",
    ];

    private readonly AutopilotSettings _settings;
    private readonly ICockpitHost _host;
    private readonly AutopilotTemplateStore _templates;
    private readonly ComboBox _ceoProfile;
    private readonly AutoCompleteBox _ceoModel;
    // Internal, not private: AC-254's settings tests drive these directly the way a real Save click would, rather
    // than reach into Avalonia's visual tree to find them by row label.
    internal readonly ComboBox CeoValidationProfileBox;
    internal readonly AutoCompleteBox CeoValidationModelBox;
    private readonly ComboBox _costStrategy;
    private readonly NumericUpDown _maxAttempts;
    private readonly NumericUpDown _maxConcurrent;
    private readonly ComboBox _autonomy;
    // One box per tracker the operator could start from — the installed ones plus the two Autopilot ships a default
    // for, so a tracker it knows nothing about is still configurable rather than silently ungated.
    private readonly Dictionary<string, TextBox> _executableStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _templateList = new() { Spacing = 0 };

    // The dialog's pages, filled by _Section in the order they are declared below — the rail shows the titles and
    // asks for one back by index, so the two lists are built together and cannot drift apart.
    private readonly List<string> _sectionTitles = [];
    private readonly List<Control> _sections = [];

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
            // offers a choice, so the field stays inert until then.
            IsEnabled = false,
        };

        // AC-254: the validator's own profile/model, independent of planning's — blank (no selection/text) means
        // "same as planning" and stays that way even if the planning pair later changes.
        CeoValidationProfileBox = new ComboBox
        {
            Width = 320,
            PlaceholderText = "Same as planning",
        };
        CeoValidationProfileBox.SelectionChanged += (_, _) => _OnValidationProfileChanged();

        CeoValidationModelBox = new AutoCompleteBox
        {
            Width = 320,
            PlaceholderText = "Model — blank = same as planning",
            Text = settings.CeoValidationModelOverride() ?? string.Empty,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 0,
            // Unlike the planning model box, this stays enabled with no profile picked: it still applies once the
            // run falls back to whichever profile planning is on.
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

        foreach (var trackerId in host.TrackerProviders.Select(provider => provider.TrackerId)
                     .Concat(AutopilotSettings.TrackersWithADefaultStage)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            _executableStages[trackerId] = _Text(settings.ExecutableStage(trackerId));
        }

        var ceo = _Section("CEO (planning)");
        ceo.Children.Add(_Hint("The profile and model the CEO plans the work with. A strong reasoning model (Opus) is recommended. Blank model uses the profile's own default."));
        ceo.Children.Add(_Row("CEO profile", _ceoProfile));
        ceo.Children.Add(_Row("CEO model", _ceoModel));
        ceo.Children.Add(_Hint("The profile and model the CEO validates each finished step with (AC-254) — the high-frequency, growing-context part of a run. A cheaper model here is a real lever; leave blank to keep following planning."));
        ceo.Children.Add(_Row("Validation profile", CeoValidationProfileBox));
        ceo.Children.Add(_Row("Validation model", CeoValidationModelBox));

        var cost = _Section("Cost & tokens");
        cost.Children.Add(_Hint("How hard the CEO leans on cost when it picks a model per step. Balanced is the recommended default; the CEO always fits the model to the work, this only moves where the line between a local free model and a paid one sits."));
        cost.Children.Add(_Row("Cost strategy", _costStrategy));

        var safety = _Section("Run safety");
        safety.Children.Add(_Hint("Caps the operator keeps regardless of what the CEO plans."));
        safety.Children.Add(_Row("Max rework attempts per step", _maxAttempts));
        safety.Children.Add(_Row("Runs at once (rest queue up)", _maxConcurrent));
        safety.Children.Add(_Row("Autonomy (permission mode)", _autonomy));
        safety.Children.Add(_Hint("How autonomous a run is on the CLI side; the host still gates shell and egress. bypassPermissions = works without asking before edits."));
        foreach (var (trackerId, box) in _executableStages)
        {
            safety.Children.Add(_Row($"{trackerId} starts from", box));
        }

        safety.Children.Add(_Hint("Which stage — or, on a tracker without stages, which label — means someone has judged an item ready to be worked on. Autopilot refuses anything else and says so on the issue, so the tracker's own gate decides what is executable rather than the ticket text claiming it about itself. Leave a field empty to start from any stage on that tracker."));

        var templatesSection = _Section("Templates");
        templatesSection.Children.Add(_Hint("Goal/brief templates you can start a run from in the plan flow. Builtin and plugin templates you can edit (kept as an override) and reset to their default; your own you can also delete."));
        var newTemplate = new Button
        {
            Content = "+ New template",
            Padding = new Thickness(11, 5),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        newTemplate.Click += (_, _) => _EditTemplate(null);
        templatesSection.Children.Add(newTemplate);
        templatesSection.Children.Add(_templateList);
        templatesSection.Children.Add(_Hint(
            "Placeholders you can use in a body (filled from the triggering issue and your input):\n" +
            "{{issue.id}} — the tracker's issue id, e.g. AC-513.\n" +
            "{{issue.title}} — the issue's title.\n" +
            "{{issue.description}} — the full description; empty if the tracker gave none.\n" +
            "{{issue.url}} — link to the issue.\n" +
            "{{issue.tracker}} — which tracker it came from, e.g. youtrack.\n" +
            "{{input.<name>}} — an operator-supplied value by name, e.g. {{input.branch}}; only filled for a name you actually ask for."));

        // Re-render the list whenever a template changes (created, edited, deleted, reset) so the section stays in step
        // with the store, the same way the plan surface tracks its queue/history.
        _templates.Changed += _OnTemplatesChanged;
        DetachedFromVisualTree += (_, _) => _templates.Changed -= _OnTemplatesChanged;
        _RenderTemplates();

        ShowSection(0);
    }

    public IReadOnlyList<string> SectionTitles => _sectionTitles;

    public void ShowSection(int index) => Content = _sections[index];

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

        // Border.tag — the theme's shape for a label that classifies the thing beside it. The colour stays here
        // because that is the part that actually differs between a builtin, a plugin and a user template.
        return new Border
        {
            Classes = { "tag" },
            Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = _Brush(key) },
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

        // No key: this is per template, and two templates can share a name — a key would collapse them into one window.
        _ = _host.ShowDialogAsync(isNew ? "New template" : $"Edit “{template!.Name}”", () =>
        {
            // Button.Accent, not a hand-mixed copy of it: the theme owns the fill, the ink on that fill and the
            // corner. The ink used to be a near-black tuned to the orange accent, which stayed behind on the blue.
            var save = new Button
            {
                Classes = { "Accent" },
                Content = "Save",
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
                Classes = { "Ghost" },
                Content = "Cancel",
                Margin = new Thickness(0, 0, 8, 0),
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

            // Validation's profile picker, unlike planning's, has no "fall back to the first profile" default: an
            // unset override leaves SelectedItem null so the placeholder keeps reading "same as planning".
            CeoValidationProfileBox.ItemsSource = labels;
            var savedValidation = _settings.CeoValidationProfileLabelOverride();
            CeoValidationProfileBox.SelectedItem = !string.IsNullOrWhiteSpace(savedValidation) && labels.Contains(savedValidation)
                ? savedValidation
                : null;
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

    // Same model-suggestions lookup as planning's, keyed to whichever profile validation would actually run on right
    // now — its own pick, or planning's when validation has none, since that is what it falls back to at runtime.
    private void _OnValidationProfileChanged()
    {
        var label = CeoValidationProfileBox.SelectedItem as string ?? _ceoProfile.SelectedItem as string;
        var suggestions = _profiles.FirstOrDefault(profile => profile.Label == label)?.ModelSuggestions;
        CeoValidationModelBox.ItemsSource = suggestions is { Count: > 0 } ? suggestions : null;
    }

    public bool Save()
    {
        _settings.SetCeoProfileLabel(_ceoProfile.SelectedItem as string);
        _settings.SetCeoModel(_ceoModel.IsEnabled ? _Trimmed(_ceoModel.Text) : null);
        _settings.SetCeoValidationProfileLabel(CeoValidationProfileBox.SelectedItem as string);
        _settings.SetCeoValidationModel(_Trimmed(CeoValidationModelBox.Text));
        _settings.SetCostStrategy(_costStrategy.SelectedIndex >= 0 ? (AutopilotCostStrategy)_costStrategy.SelectedIndex : AutopilotCostStrategy.Balanced);
        _settings.SetMaxSelfFixAttempts((int)(_maxAttempts.Value ?? 2));
        _settings.SetMaxConcurrentRuns((int)(_maxConcurrent.Value ?? 1));
        _settings.SetAutonomyMode(_autonomy.SelectedItem as string ?? AutopilotSettings.DefaultAutonomyMode);
        foreach (var (trackerId, box) in _executableStages)
        {
            _settings.SetExecutableStage(trackerId, box.Text?.Trim() ?? string.Empty);
        }

        return true;
    }

    private static string? _Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TextBox _Text(string value) => new() { Text = value, Width = 220 };

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

    // Starts a page, registers it under its rail title, and hands it back to be filled — the page keeps its own
    // heading, as the Options sub-pages do, so it still says what it is once it is the only thing on screen.
    private StackPanel _Section(string title)
    {
        var section = new StackPanel { Margin = new Thickness(4), Spacing = 10 };
        section.Children.Add(_Header(title));
        _sectionTitles.Add(title);
        _sections.Add(section);
        return section;
    }

    private static TextBlock _Header(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) =>
        new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextFaintBrush") };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
