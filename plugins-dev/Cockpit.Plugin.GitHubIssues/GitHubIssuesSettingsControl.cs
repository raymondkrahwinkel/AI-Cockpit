using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager), built in code. Toggles between
/// the GitHub CLI mode (an owner whose repos to search) and the single-repository HTTP mode (owner/name +
/// optional token), and edits the prompt template. It implements <see cref="IPluginSettingsView"/>, so the
/// host renders the Save/Close footer and <see cref="Save"/> persists on Save (the host then closes the dialog).
/// </summary>
internal sealed class GitHubIssuesSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitHubIssuesSettings _settings;
    private readonly CheckBox _useGh;
    private readonly TextBox _ghOwner;
    private readonly TextBox _owner;
    private readonly TextBox _repo;
    private readonly TextBox _token;
    private readonly TextBox _template;

    public GitHubIssuesSettingsControl(GitHubIssuesSettings settings)
    {
        _settings = settings;

        _useGh = new CheckBox
        {
            Content = "Use local GitHub CLI (gh) — lists open issues across all your repos",
            IsChecked = settings.UseGitHubCli,
        };
        var useGhRow = SettingsHelpRow.Build(_useGh, "Use the installed `gh` CLI (uses your existing gh login, no token) instead of a single-repo HTTP call.");

        _ghOwner = new TextBox { Text = settings.GhOwner, PlaceholderText = "@me (or an org / user)" };
        var ghPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _Label("Owner (whose repositories to search)"),
                SettingsHelpRow.Build(_ghOwner, "Owner (user or org, e.g. \"octocat\" or \"@me\" for yourself) whose repositories to search — cross-repo, unlike the single owner/repo below."),
                _Hint("Uses your existing gh login — no token needed."),
            },
        };

        _owner = new TextBox { Text = settings.Owner, PlaceholderText = "owner (e.g. octocat)" };
        _repo = new TextBox { Text = settings.Repo, PlaceholderText = "repository (e.g. hello-world)" };
        _token = new TextBox { Text = settings.Token, PlaceholderText = "personal access token (optional)", PasswordChar = '•' };
        var httpPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _Label("Repository owner"),
                SettingsHelpRow.Build(_owner, "The account or org name from the repository's URL, e.g. the \"owner\" in github.com/owner/repo."),
                _Label("Repository name"),
                SettingsHelpRow.Build(_repo, "The repository name — the second segment of the repository's URL, e.g. the \"repo\" in github.com/owner/repo."),
                _Label("Access token (optional — for private repos or a higher rate limit)"),
                SettingsHelpRow.Build(_token, "Personal access token. Create at github.com/settings/tokens (classic: scope `repo` for private repos; fine-grained: Issues/Pull requests read). Optional for public repos."),
            },
        };

        void SyncMode()
        {
            var gh = _useGh.IsChecked == true;
            ghPanel.IsVisible = gh;
            httpPanel.IsVisible = !gh;
        }

        _useGh.IsCheckedChanged += (_, _) => SyncMode();
        SyncMode();

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
                    useGhRow,
                    ghPanel,
                    httpPanel,
                    _Label("Prompt template — placeholders: {number} {title} {url} {owner} {repo} {body}"),
                    SettingsHelpRow.Build(_template, "Prompt inserted when you click an issue. Placeholders: {number} {title} {url} {owner} {repo} {body}."),
                },
            },
        };
    }

    /// <summary>Persists every field to the plugin's storage; always succeeds, so the host closes the dialog.</summary>
    public bool Save()
    {
        _settings.UseGitHubCli = _useGh.IsChecked == true;
        _settings.GhOwner = string.IsNullOrWhiteSpace(_ghOwner.Text) ? "@me" : _ghOwner.Text.Trim();
        _settings.Owner = _owner.Text?.Trim() ?? string.Empty;
        _settings.Repo = _repo.Text?.Trim() ?? string.Empty;
        _settings.Token = _token.Text?.Trim() ?? string.Empty;
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
