using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The settings view offers its four groups to the host's navigation rail (AC-316) instead of stacking them into one
/// scroll. What matters is that the groups are the ones the settings were already written in, in that order, and that
/// asking for one puts exactly that group on screen — nothing moved, nothing was renamed.
/// </summary>
[Collection("avalonia")]
public class AutopilotSettingsSectionsTests
{
    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through JSON, the way the host's real storage does.</summary>
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

        control.SectionTitles.Should().Equal("CEO (planning)", "Cost & tokens", "Run safety", "Templates");
    }

    [Fact]
    public void ShowingASection_PutsThatGroupOnScreen()
    {
        var control = _Control();

        for (int index = 0; index < control.SectionTitles.Count; index++)
        {
            control.ShowSection(index);

            var page = control.Content.Should().BeOfType<StackPanel>().Subject;
            page.Children[0].Should().BeOfType<TextBlock>()
                .Which.Text.Should().Be(control.SectionTitles[index],
                    "a section keeps its own heading, so it still says what it is once it is the only thing on screen");
        }
    }

    [Fact]
    public void TheDialogOpensOnTheFirstSection()
    {
        var control = _Control();

        control.Content.Should().BeOfType<StackPanel>()
            .Which.Children[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().Be("CEO (planning)");
    }

    private static AutopilotSettingsControl _Control()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }
}
