using Avalonia.Controls;
using Cockpit.Plugin.Discord.UI;

namespace Cockpit.Plugin.Discord.Tests;

[Collection("avalonia")]
public class DiscordSettingsControlStagingTests
{
    [Fact]
    public void WithoutALinkedUser_TheLinkPromptAndRemainingSettingsStayVisible()
    {
        var view = new DiscordChannelSettingsControl(
            new FakeCockpitUiHost(),
            new DiscordChannelSettings(new FakePluginStorage()));

        var controls = _Descendants(view).ToList();

        Assert.Contains(
            controls.OfType<TextBlock>(),
            text => text.IsVisible && text.Text == "Link your own account to receive messages.");
        Assert.Equal(2, controls.OfType<TextBox>().Count());
        Assert.Single(controls.OfType<ComboBox>());
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
