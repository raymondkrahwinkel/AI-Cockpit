using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager), built in code. Toggles between
/// the GitHub CLI mode (an owner whose repos to search) and the single-repository HTTP mode (owner/name +
/// optional token), and edits the prompt template. Saving writes through to the host's per-plugin storage.
/// </summary>
internal sealed class GitHubIssuesSettingsControl : UserControl
{
    public GitHubIssuesSettingsControl(GitHubIssuesSettings settings)
    {
        var useGh = new CheckBox
        {
            Content = "Use local GitHub CLI (gh) — lists open issues across all your repos",
            IsChecked = settings.UseGitHubCli,
        };

        var ghOwner = new TextBox { Text = settings.GhOwner, PlaceholderText = "@me (or an org / user)" };
        var ghPanel = new StackPanel
        {
            Spacing = 6,
            Children = { _Label("Owner (whose repositories to search)"), ghOwner, _Hint("Uses your existing gh login — no token needed.") },
        };

        var owner = new TextBox { Text = settings.Owner, PlaceholderText = "owner (e.g. octocat)" };
        var repo = new TextBox { Text = settings.Repo, PlaceholderText = "repository (e.g. hello-world)" };
        var token = new TextBox { Text = settings.Token, PlaceholderText = "personal access token (optional)", PasswordChar = '•' };
        var httpPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _Label("Repository owner"), owner,
                _Label("Repository name"), repo,
                _Label("Access token (optional — for private repos or a higher rate limit)"), token,
            },
        };

        void SyncMode()
        {
            var gh = useGh.IsChecked == true;
            ghPanel.IsVisible = gh;
            httpPanel.IsVisible = !gh;
        }

        useGh.IsCheckedChanged += (_, _) => SyncMode();
        SyncMode();

        var template = new TextBox
        {
            Text = settings.Template,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
        };
        var status = new TextBlock { FontSize = 11 };

        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            settings.UseGitHubCli = useGh.IsChecked == true;
            settings.GhOwner = string.IsNullOrWhiteSpace(ghOwner.Text) ? "@me" : ghOwner.Text.Trim();
            settings.Owner = owner.Text?.Trim() ?? string.Empty;
            settings.Repo = repo.Text?.Trim() ?? string.Empty;
            settings.Token = token.Text?.Trim() ?? string.Empty;
            settings.Template = string.IsNullOrWhiteSpace(template.Text) ? PromptTemplate.Default : template.Text;
            status.Text = "✓ Saved";
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    useGh,
                    ghPanel,
                    httpPanel,
                    _Label("Prompt template — placeholders: {number} {title} {url} {owner} {repo} {body}"),
                    template,
                    save,
                    status,
                },
            },
        };
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
