using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

// The plugin's settings view (opened from the gear in the plugin manager), built in code. Toggles between
// the GitHub CLI mode (an owner whose repos to search) and the single-repository HTTP mode (owner/name +
// optional token), and edits the prompt template. It implements `IPluginSettingsView`, so the
// host renders the Save/Close footer and `Save` persists on Save (the host then closes the dialog).
internal sealed class GitHubPullRequestsSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitHubPullRequestsSettings _settings;
    private readonly CheckBox _useGh;
    private readonly TextBox _ghOwner;
    private readonly TextBox _owner;
    private readonly TextBox _repo;
    private readonly TextBox _token;
    private readonly TextBox _template;
    private readonly TextBox _repoFilter;
    private readonly TextBox _watchedRepos;
    private readonly CheckBox _watchInvolved;
    private readonly CheckBox _notifyOnReviewRequests;
    private readonly CheckBox _mcpEnabled;

    public GitHubPullRequestsSettingsControl(ICockpitHost host, GitHubPullRequestsSettings settings)
    {
        _settings = settings;

        _useGh = new CheckBox
        {
            Content = "Use local GitHub CLI (gh) — lists open pull requests across all your repos",
            IsChecked = settings.UseGitHubCli,
        };
        var useGhRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { _useGh, host.CreateHelpHint("setup", "connect") },
        };

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
                _LabelWithHelp(host, "Owner (whose repositories to search)", "owner-repo"),
                _ghOwner,
                _Hint("Uses your existing gh login — no token needed."),
                _notifyOnReviewRequests,
                _Hint("Shows a toast with an \"Open in browser\" button the moment a pull request is assigned to you for review. The requests themselves are always listed under \"Review requested\", whether this is on or not."),
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
                _LabelWithHelp(host, "Repository owner", "owner-repo"),
                _owner,
                _Label("Repository name"),
                _repo,
                _LabelWithHelp(host, "Access token (optional — for private repos or a higher rate limit)", "token-scope"),
                _token,
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

        _watchInvolved = new CheckBox
        {
            Content = "Watch every repository I'm involved with",
            IsChecked = settings.WatchEverythingIAmInvolvedWith,
        };

        _template = new TextBox
        {
            Text = settings.Template,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
        };

        _mcpEnabled = new CheckBox
        {
            Content = "Let sessions ask for pull request status (get_pr_status MCP tool)",
            IsChecked = settings.McpEnabled,
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
                    _LabelWithHelp(host, "Beyond your own pull requests", "watching"),
                    _watchInvolved,
                    _Hint("The rest of this list answers \"which pull requests are mine\" — authored by you, assigned to you, waiting on your review. A project you are responsible for asks a different question: what is open here, whoever opened it."),
                    _Label("Watch these repositories as well (optional)"),
                    _watchedRepos,
                    _Hint("For repositories you are NOT involved with. One owner (acme: every repo of that user or org) or owner/repo (just the one) per line. Unnecessary when the box above is ticked."),
                    _Label("Only these repositories (optional)"),
                    _repoFilter,
                    _Hint("Limit the list to specific repositories — one owner/repo per line (or comma-separated), e.g. octocat/hello-world. Leave blank to show pull requests from all your repositories."),
                    _Label("Prompt template — placeholders: {number} {title} {url} {owner} {repo} {body} {author}"),
                    _template,
                    _Hint("{number} the pull request number. {title} its title. {url} link to it. {owner}/{repo} the repository. {body} the full description, \"(no description)\" when empty. {author} who opened it, \"(unknown)\" when GitHub does not give one."),
                    _Label("Agent tools"),
                    _mcpEnabled,
                    _Hint("Exposes get_pr_status over MCP so an agent session or the assistant can ask for a pull request's checks, mergeable state, review decision and title — cached briefly so several sessions watching the same PR share one lookup."),
                },
            },
        };
    }

    // Hands the host every field to write; always succeeds, so the host closes the dialog. AC-1004, criterion 3:
    // the old `Save()` was these property writes and nothing else. The plugin's one side effect on a save — the
    // pull-request list refreshing (`PullRequestRefreshSource`) — hangs on `ICockpitHost.OnSettingsSaved`, which
    // the host raises after this write, so a refresh never runs against the settings being replaced.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = _Commit;
        error = null;
        return true;
    }

    private void _Commit()
    {
        _settings.UseGitHubCli = _useGh.IsChecked == true;
        _settings.GhOwner = string.IsNullOrWhiteSpace(_ghOwner.Text) ? "@me" : _ghOwner.Text.Trim();
        _settings.NotifyOnReviewRequests = _notifyOnReviewRequests.IsChecked == true;
        _settings.Owner = _owner.Text?.Trim() ?? string.Empty;
        _settings.Repo = _repo.Text?.Trim() ?? string.Empty;
        _settings.Token = _token.Text?.Trim() ?? string.Empty;
        _settings.RepoFilter = _repoFilter.Text?.Trim() ?? string.Empty;
        _settings.WatchedRepos = _watchedRepos.Text?.Trim() ?? string.Empty;
        _settings.WatchEverythingIAmInvolvedWith = _watchInvolved.IsChecked == true;
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
        _settings.McpEnabled = _mcpEnabled.IsChecked == true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    // AC-1033: a label with the SDK-drawn `?` beside it, pointing at this plugin's own setup walkthrough —
    // replaces the old hand-rolled hover tooltip (SettingsHelpRow) field by field.
    private static Control _LabelWithHelp(ICockpitHost host, string text, string sectionId) => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 6, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, host.CreateHelpHint("setup", sectionId) },
    };
}
