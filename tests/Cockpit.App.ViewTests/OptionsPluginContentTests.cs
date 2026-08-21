using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1011: selecting a plugin's row under PLUGINS left the detail pane empty. The content ScrollViewer
/// (OptionsDialog._BuildPluginContent) bound its IsVisible to CategoryNav via ElementName synchronously, before
/// the ScrollViewer was attached to the visual tree — an ElementName binding needs a NameScope, which a
/// code-behind element only gets once attached, so `.Bind()` threw and the row's content was never added.
/// This renders a real OptionsDialog with a registered plugin and selects its row, so the gap can't hide behind
/// viewmodel-only tests again.
/// </summary>
[Collection("avalonia")]
public class OptionsPluginContentTests
{
    [Fact]
    public void SelectingAPluginRow_ShowsItsContent() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        var sink = (IPluginContributionSink)vm;
        sink.AddPluginSettings("diagram", "Diagram, Whiteboard & Wireframe", () => new TextBlock { Text = "diagram settings" });
        vm.BeginOptionsEdit();

        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var pluginItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "plugin:diagram");

        nav.SelectedItem = pluginItem;
        dialog.UpdateLayout();

        var page = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => sv.Tag as string == "plugin:diagram");
        Assert.True(page.IsEffectivelyVisible);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), tb => tb.Text == "diagram settings");

        dialog.Close();
    });
}
