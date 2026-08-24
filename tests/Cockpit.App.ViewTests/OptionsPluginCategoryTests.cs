using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1030: a plugin can declare which Options sidebar group its settings row lands in, so channel plugins
/// (Discord/Slack) land under "Assistant Plugins" instead of the generic PLUGINS group Docker/Kubernetes/Git
/// status share. A plugin that declares nothing keeps landing under PLUGINS, unchanged.
/// </summary>
[Collection("avalonia")]
public class OptionsPluginCategoryTests
{
    [Fact]
    public void ADeclaredCategory_GetsItsOwnGroup_BeforeTheDefaultPluginsGroup() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        var sink = (IPluginContributionSink)vm;
        sink.AddPluginSettings("discord", "Discord", () => new TextBlock { Text = "discord settings" }, "Assistant Plugins");
        sink.AddPluginSettings("docker", "Docker", () => new TextBlock { Text = "docker settings" });
        vm.BeginOptionsEdit();

        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var headers = nav.Items.OfType<ListBoxItem>()
            .Where(item => item.Classes.Contains("navGroupHeader"))
            .Select(item => ((TextBlock)item.Content!).Text)
            .ToList();

        // WORKING/VOICE & ASSISTANT/SYSTEM are the static groups from the .axaml; Assistant Plugins and the
        // PLUGINS catch-all are the two dynamic ones this test cares about, and in that fixed order.
        Assert.Equal(["WORKING", "VOICE & ASSISTANT", "SYSTEM", "ASSISTANT PLUGINS", "PLUGINS"], headers);

        dialog.Close();
    });

    [Fact]
    public void TheDeepLink_StillFindsARowInADeclaredCategory() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        var sink = (IPluginContributionSink)vm;
        sink.AddPluginSettings("discord", "Discord", () => new TextBlock { Text = "discord settings" }, "Assistant Plugins");
        vm.BeginOptionsEdit();

        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();

        dialog.SelectCategory("plugin:discord");
        dialog.UpdateLayout();

        var page = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => sv.Tag as string == "plugin:discord");
        Assert.True(page.IsEffectivelyVisible);

        dialog.Close();
    });
}
