using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-521, migrated for AC-1033: this field's help used to live in a `SettingsHelpRow` hover tooltip; the
// control never grew a host.CreateHelpHint for it (only the other settings did), so the placeholder
// documentation still lives in the control's own inline hint text below the field. Both sides are read from the
// real thing rather than repeated by hand: the "documented" set comes off the actual rendered control (its
// visible label plus its inline hint), and the "replaced" set is parsed out of `PromptTemplate`'s own
// `.Replace("{...}", ...)` calls — a hardcoded list here could carry the same mistake the production code does.
// This plugin has no branch-pattern field (a pull request is already a branch), so unlike the
// YouTrack/GitHub-Issues counterparts there is only the one placeholder set to pin.
[Collection("avalonia")]
public class SettingsControlPlaceholderHelpTests
{
    [Fact]
    public void PromptTemplateHelp_DocumentsExactlyThePlaceholdersRenderReplaces() => HeadlessAvalonia.Run(() =>
    {
        const string marker = "AC-521-MARKER";
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { Template = marker };
        var view = new GitHubPullRequestsSettingsControl(new TestBadgeHost(), settings);
        var window = new Window { Content = view };
        window.Show();
        window.UpdateLayout();

        var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == marker);
        var panel = (StackPanel)box.Parent!;
        var index = panel.Children.IndexOf(box);
        var label = ((TextBlock)panel.Children[index - 1]).Text ?? string.Empty;
        var hint = ((TextBlock)panel.Children[index + 1]).Text ?? string.Empty;

        window.Close();

        var documented = Regex.Matches($"{label} {hint}", @"\{[A-Za-z]+\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        var path = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.GitHubPullRequests", "PromptTemplate.cs");
        var source = File.ReadAllText(path);
        var replaced = Regex.Matches(source, @"\.Replace\(""(\{[A-Za-z]+\})""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(replaced, documented);
    });
}
