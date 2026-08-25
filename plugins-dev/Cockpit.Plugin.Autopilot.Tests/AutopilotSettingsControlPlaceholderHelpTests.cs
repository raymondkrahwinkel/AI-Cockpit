using System.Text.RegularExpressions;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-521: a placeholder documented in the help that the resolver does not actually fill (or the reverse) is the
// gap this closes. Both sides are read from the real thing, so a hardcoded list can't carry production's mistake.
// `{{input.<name>}}` is unbounded, so the resolver's own prefix literal is compared against the help text's spelling instead of a fixed list.
[Collection("avalonia")]
public class AutopilotSettingsControlPlaceholderHelpTests
{
    [Fact]
    public void TemplatesHelp_DocumentsExactlyThePlaceholdersResolveFills()
    {
        var control = _Control();
        control.ShowSection(3); // "Templates" — see AutopilotSettingsSectionsTests.

        var page = Assert.IsType<StackPanel>(control.Content);
        var help = page.Children.OfType<TextBlock>().Single(block => (block.Text ?? string.Empty).StartsWith("Placeholders you can use in a body"));

        var documented = Regex.Matches(help.Text ?? string.Empty, @"\{\{(issue\.[a-zA-Z]+|input\.<name>)\}\}")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(_ReplacedPlaceholders(), documented);
    }

    private static HashSet<string> _ReplacedPlaceholders()
    {
        var path = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.Autopilot", "AutopilotTemplateResolver.cs");
        var source = File.ReadAllText(path);

        var issueTokens = Regex.Matches(source, @"\[""(issue\.[a-zA-Z]+)""\]\s*=")
            .Select(m => "{{" + m.Groups[1].Value + "}}");

        var inputPrefix = Regex.Match(source, @"StartsWith\(""([^""]+)"",\s*StringComparison\.Ordinal\)");
        if (!inputPrefix.Success)
        {
            throw new InvalidOperationException(
                "AutopilotTemplateResolver no longer has a StartsWith(\"...\", StringComparison.Ordinal) prefix check " +
                "— the {{input.<name>}} derivation in this test needs updating to match.");
        }

        return issueTokens.Append("{{" + inputPrefix.Groups[1].Value + "<name>}}").ToHashSet(StringComparer.Ordinal);
    }

    private static AutopilotSettingsControl _Control()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _data[key] = value;
    }
}
