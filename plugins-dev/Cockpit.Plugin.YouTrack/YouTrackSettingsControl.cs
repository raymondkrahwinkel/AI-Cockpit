using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

// The plugin's settings view (opened from the gear in the plugin manager), built in code: a manageable list
// of `YouTrackInstance` rows (add/remove, each with its own base URL/token/default project — #48)
// and the shared, editable prompt template. Implements `IPluginSettingsView`, so the host renders
// the Save/Close footer and `Save` persists on Save (the host then closes the dialog).
internal sealed class YouTrackSettingsControl : UserControl, IPluginSettingsView
{
    private readonly YouTrackSettings _settings;
    private readonly ICockpitHost _host;
    private readonly StackPanel _instancesPanel;
    private readonly List<YouTrackInstanceRowControl> _rows = [];
    private readonly TextBox _template;
    private readonly TextBox _pickerQuery;
    private readonly TextBox _branchPattern;
    private readonly CheckBox _autoAttachImages;

    public YouTrackSettingsControl(ICockpitHost host, YouTrackSettings settings)
    {
        _settings = settings;
        _host = host;

        _autoAttachImages = new CheckBox
        {
            Content = "Automatically attach sent images to created/updated issues",
            IsChecked = settings.AutoAttachImages,
        };

        _instancesPanel = new StackPanel();

        var existingInstances = settings.Instances;
        if (existingInstances.Count == 0)
        {
            _AddRow(new YouTrackInstance(string.Empty, string.Empty, string.Empty, string.Empty));
        }
        else
        {
            foreach (var instance in existingInstances)
            {
                _AddRow(instance);
            }
        }

        var addInstance = new Button { Content = "+ Add instance" };
        addInstance.Click += (_, _) => _AddRow(new YouTrackInstance(string.Empty, string.Empty, string.Empty, string.Empty));

        _pickerQuery = new TextBox { Text = settings.PickerQuery, PlaceholderText = "#Unresolved" };
        _branchPattern = new TextBox { Text = settings.BranchPattern, PlaceholderText = BranchName.DefaultPattern };

        _template = new TextBox
        {
            Text = settings.Template,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    _Label("YouTrack instances"),
                    _Hint("Each instance is a separate YouTrack (cloud or self-hosted) with its own base URL and permanent token. Pick one in the issues dialog."),
                    _instancesPanel,
                    addInstance,
                    _Label("Images"),
                    _autoAttachImages,
                    _Hint("When on, a screenshot you send with a message is attached to the issue the agent creates or updates in that same turn — no per-session toggle. The agent can also attach explicitly with the attach_message_images_to_issue tool."),
                    _LabelRow("Which issues the session picker shows (YouTrack query)", host.CreateHelpHint("setup", "picker-query")),
                    _pickerQuery,

                    _LabelRow("Branch name pattern", host.CreateHelpHint("setup", "branch-pattern")),
                    _branchPattern,

                    _LabelRow("Prompt template — placeholders: {id} {idReadable} {summary} {url} {project} {description}", host.CreateHelpHint("setup", "prompt-template")),
                    _template,
                },
            },
        };
    }

    private void _AddRow(YouTrackInstance instance)
    {
        var row = new YouTrackInstanceRowControl(_host, instance);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _instancesPanel.Children.Remove(row);
        };
        _rows.Add(row);
        _instancesPanel.Children.Add(row);
    }

    // Hands the host every non-blank instance row plus the template to write; always succeeds, so the host closes
    // the dialog. AC-1004, criterion 3: the old `Save()` was these property writes and nothing else — no side
    // effect to place, and this plugin subscribes to no settings-saved signal either.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = _Commit;
        error = null;
        return true;
    }

    private void _Commit()
    {
        _settings.Instances = _rows.Where(row => !row.IsBlank).Select(row => row.ToInstance()).ToList();
        _settings.AutoAttachImages = _autoAttachImages.IsChecked ?? true;
        _settings.PickerQuery = string.IsNullOrWhiteSpace(_pickerQuery.Text) ? "#Unresolved" : _pickerQuery.Text.Trim();
        _settings.BranchPattern = string.IsNullOrWhiteSpace(_branchPattern.Text) ? BranchName.DefaultPattern : _branchPattern.Text.Trim();
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    // AC-1033: a label with the SDK-drawn "?" beside it, pointing at the section of this plugin's own setup page
    // that explains the field below — replaces the old `SettingsHelpRow` hover tooltip.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 6, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };
}
