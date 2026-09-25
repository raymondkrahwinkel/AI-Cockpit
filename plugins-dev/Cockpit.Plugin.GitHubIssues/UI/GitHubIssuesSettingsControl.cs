using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

// The plugin's settings view (opened from the gear in the plugin manager), built in code. Toggles between
// the GitHub CLI mode (an owner whose repos to search) and the single-repository HTTP mode (owner/name +
// optional token), and edits the prompt template. It implements `IPluginSettingsView`, so the
// host renders the Save/Close footer and `Save` persists on Save (the host then closes the dialog).
internal sealed class GitHubIssuesSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitHubIssuesSettings _settings;
    private readonly CheckBox _useGh;
    private readonly TextBox _ghOwner;
    private readonly TextBox _owner;
    private readonly TextBox _repo;
    private readonly TextBox _token;
    private readonly TextBox _template;
    private readonly TextBox _inProgressLabel;
    private readonly TextBox _pickerTerms;
    private readonly TextBox _branchPattern;

    public GitHubIssuesSettingsControl(ICockpitHost host, GitHubIssuesSettings settings)
    {
        _settings = settings;

        _useGh = new CheckBox
        {
            Content = "Use local GitHub CLI (gh) — lists open issues across all your repos",
            IsChecked = settings.UseGitHubCli,
        };
        var useGhRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { _useGh, host.CreateHelpHint("setup", "connect") },
        };

        _ghOwner = new TextBox { Text = settings.GhOwner, PlaceholderText = "@me (or an org / user)" };
        _inProgressLabel = new TextBox { Text = settings.InProgressLabel, PlaceholderText = "in progress (leave empty for none)" };
        _pickerTerms = new TextBox { Text = settings.PickerTerms, PlaceholderText = "-label:blocked  label:bug  no:assignee" };
        _branchPattern = new TextBox { Text = settings.BranchPattern, PlaceholderText = GitHubBranchName.DefaultPattern };

        var ghPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _LabelWithHelp(host, "Owner (whose repositories to search)", "owner-repo"),
                _ghOwner,
                _Hint("Uses your existing gh login — no token needed."),

                _Label("Which issues the session picker shows (extra search terms, optional)"),
                _pickerTerms,
                _Hint("GitHub's own search syntax, added to \"open issues\": \"-label:blocked\", \"label:bug\", \"no:assignee\". Closed issues are never offered."),

                _LabelWithHelp(host, "Branch name pattern", "branch-pattern"),
                _branchPattern,

                _LabelWithHelp(host, "Label your repos use for work in progress (optional)", "in-progress-label"),
                _inProgressLabel,
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
                    _template,
                    _Hint("{number} the issue number. {title} its title. {url} link to the issue. {owner}/{repo} the repository. {body} the full description, \"(no description)\" when empty."),
                },
            },
        };
    }

    // Hands the host every field to write; always succeeds, so the host closes the dialog. AC-1004, criterion 3:
    // the old `Save()` was these property writes and nothing else — no side effect to place, and this plugin
    // subscribes to no settings-saved signal either.
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
        _settings.Owner = _owner.Text?.Trim() ?? string.Empty;
        _settings.Repo = _repo.Text?.Trim() ?? string.Empty;
        _settings.Token = _token.Text?.Trim() ?? string.Empty;
        _settings.InProgressLabel = _inProgressLabel.Text?.Trim() ?? string.Empty;
        _settings.PickerTerms = _pickerTerms.Text?.Trim() ?? string.Empty;
        _settings.BranchPattern = string.IsNullOrWhiteSpace(_branchPattern.Text) ? GitHubBranchName.DefaultPattern : _branchPattern.Text.Trim();
        _settings.Template = string.IsNullOrWhiteSpace(_template.Text) ? PromptTemplate.Default : _template.Text;
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
