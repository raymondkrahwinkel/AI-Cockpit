using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's Options tab, built in code (no compiled XAML, so the plugin has no Avalonia-version-pinned
/// XAML to load): fields for the repository owner/name, an optional token, and the editable prompt
/// template, plus Save. It picks up the host's theme because it lives in the host's visual tree.
/// </summary>
internal sealed class GitHubIssuesOptionsControl : UserControl
{
    public GitHubIssuesOptionsControl(GitHubIssuesSettings settings)
    {
        var owner = new TextBox { Text = settings.Owner, PlaceholderText = "owner (e.g. octocat)" };
        var repo = new TextBox { Text = settings.Repo, PlaceholderText = "repository (e.g. hello-world)" };
        var token = new TextBox { Text = settings.Token, PlaceholderText = "personal access token (optional)", PasswordChar = '•' };
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
                Spacing = 6,
                Children =
                {
                    _Label("Repository owner"),
                    owner,
                    _Label("Repository name"),
                    repo,
                    _Label("Access token (optional — for private repos or a higher rate limit)"),
                    token,
                    _Label("Prompt template — placeholders: {number} {title} {url} {owner} {repo} {body}"),
                    template,
                    save,
                    status,
                },
            },
        };
    }

    private static TextBlock _Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 6, 0, 0),
    };
}
