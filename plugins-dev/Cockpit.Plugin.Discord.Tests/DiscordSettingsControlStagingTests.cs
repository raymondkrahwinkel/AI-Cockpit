using Avalonia.Controls;
using Cockpit.Plugin.Discord.UI;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Tests;

[Collection("avalonia")]
public class DiscordSettingsControlStagingTests
{
    [Fact]
    public void WithoutALinkedUser_TheLinkPromptAndRemainingSettingsStayVisible()
    {
        var storage = new FakePluginStorage();
        storage.Set("assistantChannel.audience", AssistantChannelAudience.Everyone.ToString());
        storage.Set("assistantChannel.userIds", new[] { "117", "118" });
        storage.Set("assistantChannel.verbosity", AssistantChannelVerbosity.StatusLines.ToString());
        storage.Set("discordChannelId", "42");
        AssistantChannelStorage.MigrateToSingleUser(storage, "discordChannelId");
        var view = new DiscordChannelSettingsControl(
            new FakeCockpitUiHost(),
            new DiscordChannelSettings(storage));

        var controls = _Descendants(view).ToList();

        Assert.Contains(
            controls.OfType<TextBlock>(),
            text => text.IsVisible && text.Text == "Link your own account to receive messages.");
        Assert.Equal(2, controls.OfType<TextBox>().Count());
        Assert.Equal(
            (int)AssistantChannelVerbosity.StatusLines,
            Assert.Single(controls.OfType<ComboBox>()).SelectedIndex);
    }

    private static IEnumerable<Control> _Descendants(Control control)
    {
        var children = control switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            ContentControl { Content: Control child } => [child],
            Decorator { Child: { } child } => [child],
            _ => [],
        };

        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in _Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
