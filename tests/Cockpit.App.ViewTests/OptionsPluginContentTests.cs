using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions;

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

    // AC-1078 criterion 3, the whole of it: a plugin with empty required fields says so on its own page and
    // nowhere else. Until AC-1084 every section's reason was joined into one footer line, so a plugin's complaint
    // stood in the dialog chrome instead of beside the fields it is about.
    [Fact]
    public async Task ARefusedPluginRow_StatesItsReasonOnItsOwnPage_AndNotInTheFooter() => await HeadlessAvalonia.RunAsync(async () =>
    {
        const string reason = "A bot token is required.";
        var vm = new CockpitViewModel();
        var sink = (IPluginContributionSink)vm;
        sink.AddPluginSettings("discord", "Discord", () => new _RefusingView(reason));
        vm.BeginOptionsEdit();
        vm.PluginOptionsRows.Single().EnsureContent();

        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();

        await vm.ApplyOptionsCommand.ExecuteAsync(null);
        dialog.SelectCategory(vm.OptionsApplyBlockedCategoryTag!);
        dialog.UpdateLayout();

        var page = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(sv => sv.Tag as string == "plugin:discord");
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), tb => tb.Text == reason && tb.IsEffectivelyVisible);

        // And nowhere else: no other visible label in the dialog repeats it, joined with anything or on its own.
        var elsewhere = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Where(tb => !page.GetVisualDescendants().Contains(tb))
            .Where(tb => tb.IsEffectivelyVisible && tb.Text is { } text && text.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(elsewhere);

        dialog.Close();
    });

    // Refuses whatever it is handed, so the dialog has a reason to place. The plugin's own view draws nothing
    // itself — like twelve of the seventeen real settings views, and the case that would go silent if the host
    // left reporting to the plugin.
    private sealed class _RefusingView(string reason) : TextBlock, IPluginSettingsView
    {
        public bool TryStage(out Action? commit, out string? error)
        {
            commit = null;
            error = reason;
            return false;
        }
    }
}
