using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.YouTrack.Tests;

// AC-521, migrated for AC-1041: placeholder help now lives in Docs/setup.md via host.CreateHelpHint.
[Collection("avalonia")]
public partial class SettingsControlPlaceholderHelpTests
{
    [Fact]
    public void PromptTemplateHelp_DocumentsExactlyThePlaceholdersRenderReplaces() => HeadlessAvalonia.Run(() =>
    {
        var label = _LabelFor(marker => new YouTrackSettings(new InMemoryPluginStorage()) { Template = marker });

        Assert.Equal(_ReplacedPlaceholders("PromptTemplate.cs"), _DocumentedPlaceholders(label, "prompt-template"));
    });

    [Fact]
    public void BranchPatternHelp_DocumentsExactlyThePlaceholdersBranchNameReplaces() => HeadlessAvalonia.Run(() =>
    {
        var label = _LabelFor(marker => new YouTrackSettings(new InMemoryPluginStorage()) { BranchPattern = marker });

        Assert.Equal(_ReplacedPlaceholders("BranchName.cs"), _DocumentedPlaceholders(label, "branch-pattern"));
    });

    // Builds the control with a unique marker in whichever field the caller sets on the settings object, then reads
    // back that field's own label row — so the template's help is never mistaken for the branch pattern's.
    private static string _LabelFor(Func<string, YouTrackSettings> buildSettingsWithMarker)
    {
        const string marker = "AC-521-MARKER";
        var settings = buildSettingsWithMarker(marker);
        var view = new YouTrackSettingsControl(new FakeCockpitHost(), settings);
        var window = new Window { Content = view };
        window.Show();
        window.UpdateLayout();

        var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == marker);
        var panel = (StackPanel)box.Parent!;
        var labelRow = (StackPanel)panel.Children[panel.Children.IndexOf(box) - 1];
        var label = ((TextBlock)labelRow.Children[0]).Text ?? string.Empty;

        window.Close();
        return label;
    }

    // The set of placeholders documented for `section` — the control's own label plus the matching section body
    // of this plugin's shipped Docs/setup.md, which is what host.CreateHelpHint("setup", section) points at.
    private static HashSet<string> _DocumentedPlaceholders(string label, string section)
    {
        var docsPath = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.YouTrack", "Docs", "setup.md");
        var body = _SectionBody(File.ReadAllText(docsPath), section);

        return PlaceholderRegex().Matches($"{label} {body}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    }

    // Everything between the heading carrying `{#section}` and the next heading (or end of file).
    private static string _SectionBody(string markdown, string section)
    {
        var headings = HeadingRegex().Matches(markdown).Cast<Match>().ToList();
        var start = headings.FindIndex(heading => heading.Value.Contains($"{{#{section}}}", StringComparison.Ordinal));
        Assert.True(start >= 0, $"Docs/setup.md has no heading for section '{section}'");

        var bodyStart = headings[start].Index + headings[start].Length;
        var bodyEnd = start + 1 < headings.Count ? headings[start + 1].Index : markdown.Length;
        return markdown[bodyStart..bodyEnd];
    }

    private static HashSet<string> _ReplacedPlaceholders(string fileName)
    {
        var path = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.YouTrack", fileName);
        var source = File.ReadAllText(path);
        return Regex.Matches(source, @"\.Replace\(""(\{[A-Za-z]+\})""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^#{1,6}\s.*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
