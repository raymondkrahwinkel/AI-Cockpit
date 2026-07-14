using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager), built in code. Toggles between
/// the GitHub CLI mode (an owner whose repos to search) and the single-repository HTTP mode (owner/name +
/// optional token), and edits the prompt template. It implements <see cref="IPluginSettingsView"/>, so the
/// host renders the Save/Close footer and <see cref="Save"/> persists on Save (the host then closes the dialog).
/// </summary>
internal sealed class GitHubPullRequestsSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitHubPullRequestsSettings _settings;
    private readonly CheckBox _useGh;
    private readonly TextBox _ghOwner;
    private readonly TextBox _owner;
    private readonly TextBox _repo;
    private readonly TextBox _token;
    private readonly TextBox _template;
    private readonly NumericUpDown _maxItems;
    private readonly TextBox _repoFilter;
    private readonly TextBox _watchedRepos;
    private readonly CheckBox _notifyOnReviewRequests;

    public GitHubPullRequestsSettingsControl(GitHubPullRequestsSettings settings)
    {
        _settings = settings;

        _useGh = new CheckBox
        {
            Content = "Use local GitHub CLI (gh) — lists open pull requests across all your repos",
            IsChecked = settings.UseGitHubCli,
        };
        var useGhRow = SettingsHelpRow.Build(_useGh, "Use the installed `gh` CLI (uses your existing gh login, no token) instead of a single-repo HTTP call.");

        _ghOwner = new TextBox { Text = settings.GhOwner, PlaceholderText = "@me (or an org / user)" };
        _notifyOnReviewRequests = new CheckBox
        {
            Content = "Notify me when a pull request starts waiting for my review",
            IsChecked = settings.NotifyOnReviewRequests,
        };
        var ghPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _Label("Owner (whose repositories to search)"),
                SettingsHelpRow.Build(_ghOwner, "Owner (user or org, e.g. \"octocat\" or \"@me\" for yourself) whose repositories to search — cross-repo, unlike the single owner/repo below."),
                _Hint("Uses your existing gh login — no token needed."),
                SettingsHelpRow.Build(_notifyOnReviewRequests, "Shows a toast with an \"Open in browser\" button the moment a pull request is assigned to you for review. The requests themselves are always listed under \"Review requested\" in the section, whether this is on or not."),
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

        _maxItems = new NumericUpDown
        {
            Value = settings.MaxItems,
            Minimum = 1,
            Maximum = 20,
            Increment = 1,
            FormatString = "0",
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _repoFilter = new TextBox
        {
            Text = settings.RepoFilter,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            PlaceholderText = "owner/repo per line — blank = all repositories",
        };

        _watchedRepos = new TextBox
        {
            Text = settings.WatchedRepos,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            PlaceholderText = "owner or owner/repo per line — blank = only what is mine",
        };

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
                    _Label("Show how many pull requests inline (the dialog lists them all)"),
                    SettingsHelpRow.Build(_maxItems, "How many pull requests the inline section shows under the session list (1–20). The \"View all open PRs\" dialog always lists every one."),
                    _Label("Watch these repositories — every open pull request in them, whoever opened it"),
                    SettingsHelpRow.Build(_watchedRepos, "One owner (EVE-Workbench: every repo of that user or org) or owner/repo (just the one) per line. The rest of this list is about pull requests that are yours — authored by you, assigned to you, waiting on your review. A repository you are responsible for asks a different question: what is open here, whoever opened it."),
                    _Label("Only these repositories (optional)"),
                    SettingsHelpRow.Build(_repoFilter, "Limit the list to specific repositories — one owner/repo per line (or comma-separated), e.g. octocat/hello-world. Leave blank to show pull requests from all your repositories."),
                    _Label("Prompt template — placeholders: {number} {title} {url} {owner} {repo} {body} {author}"),
                    SettingsHelpRow.Build(_template, "Prompt inserted when you click a pull request. Placeholders: {number} {title} {url} {owner} {repo} {body} {author}."),
                },
            },
        };
    }

    /// <summary>Persists every field to the plugin's storage; always succeeds, so the host closes the dialog.</summary>
    public bool Save()
    {
        _settings.UseGitHubCli = _useGh.IsChecked == true;
        _settings.GhOwner = string.IsNullOrWhiteSpace(_ghOwner.Text) ? "@me" : _ghOwner.Text.Trim();
        _settings.NotifyOnReviewRequests = _notifyOnReviewRequests.IsChecked == true;
        _settings.Owner = _owner.Text?.Trim() ?? string.Empty;
        _settings.Repo = _repo.Text?.Trim() ?? string.Empty;
        _settings.Token = _token.Text?.Trim() ?? string.Empty;
        _settings.MaxItems = (int)(_maxItems.Value ?? 5);
        _settings.RepoFilter = _repoFilter.Text?.Trim() ?? string.Empty;
        _settings.WatchedRepos = _watchedRepos.Text?.Trim() ?? string.Empty;
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
