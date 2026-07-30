using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-521: a placeholder documented in the settings view that <see cref="PromptTemplate.Render"/> or
/// <see cref="BranchName.From"/> does not actually replace (or the reverse — one they replace that stays
/// undocumented) is exactly the gap this ticket closes. Both sides are read from the real thing rather than
/// repeated by hand: the "documented" set comes off the actual rendered control (its visible label plus its "?"
/// tooltip), and the "replaced" set is parsed out of the corresponding source file's own <c>.Replace("{...}", ...)</c>
/// calls — a hardcoded list here could carry the same mistake the production code does.
/// </summary>
[Collection("avalonia")]
public class SettingsControlPlaceholderHelpTests
{
    [Fact]
    public void PromptTemplateHelp_DocumentsExactlyThePlaceholdersRenderReplaces() => HeadlessAvalonia.Run(() =>
    {
        var (label, tooltip) = _HelpFor(marker => new YouTrackSettings(new InMemoryPluginStorage()) { Template = marker });

        Assert.Equal(_ReplacedPlaceholders("PromptTemplate.cs"), _DocumentedPlaceholders(label, tooltip));
    });

    [Fact]
    public void BranchPatternHelp_DocumentsExactlyThePlaceholdersBranchNameReplaces() => HeadlessAvalonia.Run(() =>
    {
        var (label, tooltip) = _HelpFor(marker => new YouTrackSettings(new InMemoryPluginStorage()) { BranchPattern = marker });

        Assert.Equal(_ReplacedPlaceholders("BranchName.cs"), _DocumentedPlaceholders(label, tooltip));
    });

    // Builds the control with a unique marker in whichever field the caller sets on the settings object, then reads
    // back that field's own label + "?" tooltip — so the template's help is never mistaken for the branch pattern's.
    private static (string Label, string Tooltip) _HelpFor(Func<string, YouTrackSettings> buildSettingsWithMarker)
    {
        const string marker = "AC-521-MARKER";
        var settings = buildSettingsWithMarker(marker);
        var view = new YouTrackSettingsControl(settings);
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
        return (label, tooltip);
    }

    private static HashSet<string> _DocumentedPlaceholders(string label, string tooltip) =>
        Regex.Matches($"{label} {tooltip}", @"\{[A-Za-z]+\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> _ReplacedPlaceholders(string fileName)
    {
        var path = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.YouTrack", fileName);
        var source = File.ReadAllText(path);
        return Regex.Matches(source, @"\.Replace\(""(\{[A-Za-z]+\})""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
