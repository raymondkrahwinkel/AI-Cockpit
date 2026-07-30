using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// AC-521: a placeholder documented in the settings view that <see cref="PromptTemplate.Render"/> does not
/// actually replace (or the reverse — one it replaces that stays undocumented) is exactly the gap this ticket
/// closes. Both sides are read from the real thing rather than repeated by hand: the "documented" set comes off
/// the actual rendered control (its visible label plus its "?" tooltip), and the "replaced" set is parsed out of
/// <see cref="PromptTemplate"/>'s own <c>.Replace("{...}", ...)</c> calls — a hardcoded list here could carry the
/// same mistake the production code does. This plugin has no branch-pattern field (a pull request is already a
/// branch), so unlike the YouTrack/GitHub-Issues counterparts there is only the one placeholder set to pin.
/// </summary>
[Collection("avalonia")]
public class SettingsControlPlaceholderHelpTests
{
    [Fact]
    public void PromptTemplateHelp_DocumentsExactlyThePlaceholdersRenderReplaces() => HeadlessAvalonia.Run(() =>
    {
        const string marker = "AC-521-MARKER";
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { Template = marker };
        var view = new GitHubPullRequestsSettingsControl(settings);
        var window = new Window { Content = view };
        window.Show();
        window.UpdateLayout();

        var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == marker);
        var grid = (Grid)box.Parent!;
        var help = grid.Children.OfType<TextBlock>().Single();
        var tooltip = ToolTip.GetTip(help) as string ?? string.Empty;

        var panel = (StackPanel)grid.Parent!;
        var labelIndex = panel.Children.IndexOf(grid) - 1;
        var label = ((TextBlock)panel.Children[labelIndex]).Text ?? string.Empty;

        window.Close();

        var documented = Regex.Matches($"{label} {tooltip}", @"\{[A-Za-z]+\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        var path = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.GitHubPullRequests", "PromptTemplate.cs");
        var source = File.ReadAllText(path);
        var replaced = Regex.Matches(source, @"\.Replace\(""(\{[A-Za-z]+\})""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(replaced, documented);
    });
}
