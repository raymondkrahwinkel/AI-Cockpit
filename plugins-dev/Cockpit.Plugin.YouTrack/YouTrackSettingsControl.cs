using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager), built in code: a manageable list
/// of <see cref="YouTrackInstance"/> rows (add/remove, each with its own base URL/token/default project — #48)
/// and the shared, editable prompt template. Implements <see cref="IPluginSettingsView"/>, so the host renders
/// the Save/Close footer and <see cref="Save"/> persists on Save (the host then closes the dialog).
/// </summary>
internal sealed class YouTrackSettingsControl : UserControl, IPluginSettingsView
{
    private readonly YouTrackSettings _settings;
    private readonly StackPanel _instancesPanel;
    private readonly List<YouTrackInstanceRowControl> _rows = [];
    private readonly TextBox _template;

    public YouTrackSettingsControl(YouTrackSettings settings)
    {
        _settings = settings;

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
                    _Label("Prompt template — placeholders: {id} {idReadable} {summary} {url} {project} {description}"),
                    SettingsHelpRow.Build(_template, "Prompt inserted when you click an issue. Placeholders: {id} {idReadable} {summary} {url} {project} {description}."),
                },
            },
        };
    }

    private void _AddRow(YouTrackInstance instance)
    {
        var row = new YouTrackInstanceRowControl(instance);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _instancesPanel.Children.Remove(row);
        };
        _rows.Add(row);
        _instancesPanel.Children.Add(row);
    }

    /// <summary>Persists every non-blank instance row plus the template to the plugin's storage; always succeeds, so the host closes the dialog.</summary>
    public bool Save()
    {
        _settings.Instances = _rows.Where(row => !row.IsBlank).Select(row => row.ToInstance()).ToList();
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
