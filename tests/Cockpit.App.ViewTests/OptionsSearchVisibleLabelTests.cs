using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class OptionsSearchVisibleLabelTests
{
    [Fact]
    public void SearchingAVisibleStaticLabel_ShowsItsRowAndHidesTheOtherRows() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();
        vm.OptionsSearchText = "Rate windows (e.g. 5-hour, weekly — whatever the provider reports)";
        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        Assert.True(nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "appearance").IsEffectivelyVisible);
        dialog.SelectCategory("appearance");
        dialog.UpdateLayout();

        var rows = dialog.GetVisualDescendants().OfType<CheckBox>().ToDictionary(box => box.Content!.ToString()!);

        Assert.True(rows["Rate windows (e.g. 5-hour, weekly — whatever the provider reports)"].IsEffectivelyVisible);
        Assert.False(rows["Context window (ctx %)"].IsEffectivelyVisible);

        vm.OptionsSearchText = string.Empty;
        dialog.UpdateLayout();
        Assert.True(rows["Context window (ctx %)"].IsEffectivelyVisible);

        dialog.Close();
    });

    [Fact]
    public void SearchingAVisiblePluginLabel_ShowsOnlyTheMatchingPluginRow() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        ((IPluginContributionSink)vm).AddPluginSettings("sample", "Sample plugin", () => new StackPanel
        {
            Children =
            {
                new CheckBox { Content = "AC-1087 plugin-only label" },
                new CheckBox { Content = "Send diagnostic events" },
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

        dialog.Close();
    });

    [Fact]
    public void ClearingSearch_RestoresAnExistingVisibilityBinding() => HeadlessAvalonia.Run(() =>
    {
        var thresholds = new UsageThresholdsViewModel(new UsageThresholdStore());
        thresholds.LoadAsync([("claude", "Claude", [new PluginUsageSignal("weekly", "Weekly", PluginUsageSignalKind.Allowance, 80)])]).GetAwaiter().GetResult();
        var vm = new CockpitViewModel { UsageThresholdSettings = thresholds };
        vm.BeginOptionsEdit();
        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();
        vm.OptionsSearchText = "Weekly";
        vm.OptionsSearchText = string.Empty;
        dialog.UpdateLayout();

        var panel = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Warn me when a session is running out")
            .GetLogicalParent() as StackPanel;
        Assert.NotNull(panel);
        Assert.True(panel.IsEffectivelyVisible);

        thresholds.LoadAsync([]).GetAwaiter().GetResult();
        dialog.UpdateLayout();
        Assert.False(panel.IsEffectivelyVisible);

        dialog.Close();
    });

    private sealed class UsageThresholdStore : IUsageThresholdStore
    {
        public Task<UsageThresholdSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageThresholdSettings());
        public Task SaveAsync(UsageThresholdSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
