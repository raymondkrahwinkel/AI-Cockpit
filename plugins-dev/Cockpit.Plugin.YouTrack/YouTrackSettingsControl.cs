using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager), built in code: instance base
/// URL, permanent token, project short-name, an optional extra query filter, and the editable prompt
/// template. Implements <see cref="IPluginSettingsView"/>, so the host renders the Save/Close footer and
/// <see cref="Save"/> persists on Save (the host then closes the dialog).
/// </summary>
internal sealed class YouTrackSettingsControl : UserControl, IPluginSettingsView
{
    private readonly YouTrackSettings _settings;
    private readonly TextBox _instanceUrl;
    private readonly TextBox _token;
    private readonly TextBox _projectTag;
    private readonly TextBox _extraQuery;
    private readonly TextBox _template;

    public YouTrackSettingsControl(YouTrackSettings settings)
    {
        _settings = settings;

        _instanceUrl = new TextBox { Text = settings.InstanceUrl, PlaceholderText = "https://<instance>.youtrack.cloud/api" };
        _token = new TextBox { Text = settings.Token, PlaceholderText = "permanent token", PasswordChar = '•' };
        _projectTag = new TextBox { Text = settings.ProjectTag, PlaceholderText = "project short-name (e.g. PROJ)" };
        _extraQuery = new TextBox { Text = settings.ExtraQuery, PlaceholderText = "extra query filter (optional, e.g. Priority: Critical)" };
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
                    _Label("Instance base URL"),
                    SettingsHelpRow.Build(_instanceUrl, "Your YouTrack REST API base, e.g. https://<instance>.youtrack.cloud/api (self-hosted: https://<host>/youtrack/api)."),
                    _Label("Permanent token"),
                    SettingsHelpRow.Build(_token, "Permanent token. In YouTrack: profile -> Account Security -> New token (scope: YouTrack)."),
                    _Hint("Never shared or hardcoded — stored only in this cockpit's local settings."),
                    _Label("Project short-name (tag)"),
                    SettingsHelpRow.Build(_projectTag, "Project short name (e.g. the prefix in issue IDs like PROJ-123). Find it in the project's settings."),
                    _Label("Extra query filter (optional — appended to the open-issues search)"),
                    SettingsHelpRow.Build(_extraQuery, "Optional YouTrack query fragment appended to the open-issues search (e.g. \"Priority: Critical\"), using YouTrack's query syntax."),
                    _Label("Prompt template — placeholders: {id} {idReadable} {summary} {url} {project} {description}"),
                    SettingsHelpRow.Build(_template, "Prompt inserted when you click an issue. Placeholders: {id} {idReadable} {summary} {url} {project} {description}."),
                },
            },
        };
    }

    /// <summary>Persists every field to the plugin's storage; always succeeds, so the host closes the dialog.</summary>
    public bool Save()
    {
        _settings.InstanceUrl = string.IsNullOrWhiteSpace(_instanceUrl.Text) ? string.Empty : _instanceUrl.Text.Trim().TrimEnd('/');
        _settings.Token = _token.Text?.Trim() ?? string.Empty;
        _settings.ProjectTag = _projectTag.Text?.Trim() ?? string.Empty;
        _settings.ExtraQuery = _extraQuery.Text?.Trim() ?? string.Empty;
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
