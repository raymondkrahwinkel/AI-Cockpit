using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The settings view offers its four groups to the host's navigation rail (AC-316) instead of stacking them into one
// scroll. What matters is that the groups are the ones the settings were already written in, in that order, and that
// asking for one puts exactly that group on screen — nothing moved, nothing was renamed.
[Collection("avalonia")]
public class AutopilotSettingsSectionsTests
{
    // An in-memory `IPluginStorage` that round-trips through JSON, the way the host's real storage does.
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    [Fact]
    public void TheSections_AreTheGroupsTheSettingsWereAlreadyWrittenIn()
    {
        var control = _Control();

        Assert.Equal(new[] { "CEO (planning)", "Cost & tokens", "Run safety", "Templates" }, control.SectionTitles);
    }

    [Fact]
    public void ShowingASection_PutsThatGroupOnScreen()
    {
        var control = _Control();

        for (int index = 0; index < control.SectionTitles.Count; index++)
        {
            control.ShowSection(index);

            var page = Assert.IsType<StackPanel>(control.Content);
            Assert.Equal(control.SectionTitles[index], Assert.IsType<TextBlock>(page.Children[0]).Text);
        }
    }

    [Fact]
    public void TheDialogOpensOnTheFirstSection()
    {
        var control = _Control();

        Assert.Equal("CEO (planning)", Assert.IsType<TextBlock>(Assert.IsType<StackPanel>(control.Content).Children[0]).Text);
    }

    private static AutopilotSettingsControl _Control()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }
}
