using System.Text.RegularExpressions;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-521: a placeholder documented in the Templates section's help that `AutopilotTemplateResolver.Resolve`
// does not actually fill (or the reverse — one it fills that stays undocumented) is exactly the gap this ticket
// closes. Both sides are read from the real thing rather than repeated by hand: the "documented" set comes off the
// actual rendered help text, and the "replaced" set is parsed out of `AutopilotTemplateResolver`'s own
// source — its `IssueDataKeys` dictionary for the fixed `{{issue.*}}` tokens, and its own
// `token.StartsWith("...")` check for the `input.*` prefix — a hardcoded list here could carry the same
// mistake the production code does.
//
// `{{input.&lt;name&gt;}}` is not a fixed token like the others — it names a whole class of operator-supplied
// keys, unbounded at compile time. Treating it as one more literal to match would be wrong (nothing enumerates
// "every possible input name" in the source) and skipping it entirely would prove nothing about it at all. Instead
// the resolver's own prefix literal ("input.") is extracted from its `StartsWith` call and turned into the
// same generic placeholder spelling ("&lt;name&gt;") the help text uses — so if the resolver ever accepted a
// different prefix, or the help text described a different one, this test would catch the mismatch on that prefix
// itself, not merely confirm both sides mention the word "input" somewhere.
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

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _data[key] = value;
    }
}
