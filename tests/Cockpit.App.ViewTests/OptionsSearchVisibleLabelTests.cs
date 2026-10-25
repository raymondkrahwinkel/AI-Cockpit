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

    [Fact]
    public void ClearingSearch_RestoresAnExistingVisibilityBinding() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel { DiscordNotificationsEnabled = true };
        vm.BeginOptionsEdit();
        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.SelectCategory("notifications");
        dialog.UpdateLayout();
        vm.OptionsSearchText = "Discord webhook URL";
        vm.OptionsSearchText = string.Empty;
        dialog.UpdateLayout();

        var panel = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Discord webhook URL")
            .GetLogicalParent() as StackPanel;
        Assert.NotNull(panel);
        Assert.True(panel.IsEffectivelyVisible);

        vm.DiscordNotificationsEnabled = false;
        dialog.UpdateLayout();
        Assert.False(panel.IsEffectivelyVisible);

        dialog.Close();
    });

    // AC-289: declared thresholds sit once on their plugin's page, beside its own view if any, and search finds them there.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeclaredThreshold_LivesOnItsPluginsPage_WhereSearchFindsIt(bool pluginAlsoAddsSettings) => HeadlessAvalonia.Run(() =>
    {
        var thresholds = new UsageThresholdsViewModel(new UsageThresholdStore());
        thresholds.LoadAsync([("claude", "Claude", [new PluginUsageSignal("weekly", "Weekly", PluginUsageSignalKind.Allowance, 80)])], _ => "claude-provider")
            .GetAwaiter().GetResult();
        var vm = new CockpitViewModel { UsageThresholdSettings = thresholds };
        if (pluginAlsoAddsSettings)
        {
            ((IPluginContributionSink)vm).AddPluginSettings("claude-provider", "Claude Code", () => new CheckBox { Content = "Claude's own setting" });
        }

        vm.BeginOptionsEdit();
        var dialog = new OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();
        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        Assert.True(vm.HasPluginSettings("claude-provider"));
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Warn me when a session is running out");

        vm.OptionsSearchText = "Weekly";
        var pluginItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "plugin:claude-provider");
        Assert.True(pluginItem.IsEffectivelyVisible);
        vm.OptionsSearchText = string.Empty;
        nav.SelectedItem = pluginItem;
        dialog.UpdateLayout();

        var page = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Tag as string == "plugin:claude-provider");
        var texts = page.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Single(texts, text => text == "Warn me when a session is running out");
        Assert.Contains("Weekly", texts);
        Assert.Equal(pluginAlsoAddsSettings, page.GetVisualDescendants().OfType<CheckBox>().Any(box => box.Content as string == "Claude's own setting"));

        dialog.Close();
    });

    private static List<string?> _VisibleTexts(Window dialog, params string[] texts) =>
        dialog.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => texts.Contains(block.Text) && block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();

    private sealed class UsageThresholdStore : IUsageThresholdStore
    {
        public Task<UsageThresholdSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageThresholdSettings());
        public Task SaveAsync(UsageThresholdSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
