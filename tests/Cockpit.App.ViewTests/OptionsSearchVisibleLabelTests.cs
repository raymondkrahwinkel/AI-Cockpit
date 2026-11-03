using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class OptionsSearchVisibleLabelTests
{
    // Layer 2 (view behaviour): a flat label-then-box layout, as the YouTrack instance card has, keeps each label with its box.
    [Fact]
    public void SearchingAVisiblePluginLabel_ShowsOnlyTheMatchingPluginRow() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        var instanceUrl = new TextBox { Text = "https://example.youtrack.cloud/api" };
        var token = new TextBox();
        var trailingHint = new TextBlock { Text = "Applies to every session." };
        ((IPluginContributionSink)vm).AddPluginSettings("sample", "Sample plugin", () => new StackPanel
        {
            Children =
            {
                new CheckBox { Content = "AC-1087 plugin-only label" },
                new CheckBox { Content = "Send diagnostic events" },
                new TextBlock { Text = "Instance base URL" },
                instanceUrl,
                new TextBlock { Text = "Permanent token" },
                token,
                trailingHint,
            },
        });
        vm.BeginOptionsEdit();

        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();
        vm.OptionsSearchText = "AC-1087 plugin-only label";
        dialog.UpdateLayout();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var pluginItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "plugin:sample");
        Assert.True(pluginItem.IsEffectivelyVisible);
        nav.SelectedItem = pluginItem;
        dialog.UpdateLayout();
        var rows = dialog.GetVisualDescendants().OfType<CheckBox>().ToDictionary(box => box.Content!.ToString()!);

        Assert.True(rows["AC-1087 plugin-only label"].IsEffectivelyVisible);
        Assert.False(rows["Send diagnostic events"].IsEffectivelyVisible);
        Assert.Empty(_VisibleTexts(dialog, "Instance base URL", "Permanent token", "Applies to every session."));
        Assert.False(instanceUrl.IsEffectivelyVisible);

        vm.OptionsSearchText = "Instance base URL";
        dialog.UpdateLayout();
        Assert.True(instanceUrl.IsEffectivelyVisible);
        Assert.Equal(["Instance base URL"], _VisibleTexts(dialog, "Instance base URL", "Permanent token", "Applies to every session."));
        Assert.False(token.IsEffectivelyVisible);

        vm.OptionsSearchText = "Permanent token";
        dialog.UpdateLayout();
        Assert.True(token.IsEffectivelyVisible);
        Assert.True(trailingHint.IsEffectivelyVisible);

        dialog.Close();
    });

    private static List<string?> _VisibleTexts(Window dialog, params string[] texts) =>
        dialog.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => texts.Contains(block.Text) && block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();
}
