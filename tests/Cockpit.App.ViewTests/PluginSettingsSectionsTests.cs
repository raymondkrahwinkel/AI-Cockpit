using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A plugin's settings view can split itself into named sections (<see cref="IPluginSettingsSections"/>, AC-316),
/// and the host then draws the Options navigation rail beside it instead of stacking everything into one scroll.
/// These assert on what <see cref="PluginSettingsBodyBuilder"/> actually builds — the dialog itself is modal and
/// needs a running app, but the body under its footer is the part that changed.
/// </summary>
[Collection("avalonia")]
public class PluginSettingsSectionsTests
{
    [Fact]
    public void AViewWithoutSections_IsHostedFlat() => HeadlessAvalonia.Run(() =>
    {
        var view = new UserControl();

        var body = PluginSettingsBodyBuilder.Build(view);

        Assert.False(body.HasRail, "a view that does not declare sections keeps the dialog it has today");
        Assert.Same(view, Assert.IsType<Border>(Assert.IsType<ScrollViewer>(body.Content).Content).Child);
    });

    [Fact]
    public void ASingleSection_StaysFlat() => HeadlessAvalonia.Run(() =>
    {
        var body = PluginSettingsBodyBuilder.Build(new SectionedView("Everything"));

        Assert.False(body.HasRail, "a rail beside one page costs width and navigates nothing");
        Assert.IsType<ScrollViewer>(body.Content);
    });

    [Fact]
    public void TwoSections_DrawTheRailWithTheirTitlesBesideTheView() => HeadlessAvalonia.Run(() =>
    {
        var view = new SectionedView("Run safety", "Templates");

        var body = PluginSettingsBodyBuilder.Build(view);

        Assert.True(body.HasRail);
        var split = Assert.IsType<Grid>(body.Content);
        var rail = split.Children.OfType<Border>().Single();
        Assert.Contains("subnavRail", rail.Classes);

        Assert.Equal("SETTINGS", rail.GetLogicalDescendants().OfType<TextBlock>().Single().Text);

        var items = rail.GetLogicalDescendants().OfType<ListBox>().Single();
        Assert.Contains("subnav", items.Classes);
        Assert.Equivalent(new[] { "Run safety", "Templates" }, items.ItemsSource);

        // The view is still the scrolled content: it stays attached for the whole dialog, so a settings view that
        // loads on attach or unsubscribes on detach behaves exactly as it does without a rail.
        Assert.Same(view, Assert.IsType<Border>(split.Children.OfType<ScrollViewer>().Single()
            .Content).Child);
    });

    [Fact]
    public void TheDialogOpensOnTheFirstSection() => HeadlessAvalonia.Run(() =>
    {
        var view = new SectionedView("Run safety", "Templates");

        PluginSettingsBodyBuilder.Build(view);

        Assert.Equal(new[] { 0 }, view.Shown);
    });

    [Fact]
    public void PickingASection_ShowsItFromItsTop() => HeadlessAvalonia.Run(() =>
    {
        var view = new SectionedView("Run safety", "Templates");
        var split = (Grid)PluginSettingsBodyBuilder.Build(view).Content;
        var window = Show(split);

        // The host's own ScrollViewer, not the one inside the rail's ListBox template.
        var scroll = split.Children.OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();
        Assert.True(scroll.Offset.Y > 0, "the first section has to be scrolled for the reset to mean anything");

        split.Children.OfType<Border>().Single().GetLogicalDescendants().OfType<ListBox>().Single().SelectedIndex = 1;
        window.UpdateLayout();

        Assert.Equal(new[] { 0, 1 }, view.Shown);
        Assert.Equal(0, scroll.Offset.Y);

        window.Close();
    });

    [Fact]
    public void SectionsGoingAway_DoNotAskTheViewForOne() => HeadlessAvalonia.Run(() =>
    {
        var titles = new ObservableCollection<string> { "Run safety", "Templates" };
        var view = new SectionedView(titles);
        var split = (Grid)PluginSettingsBodyBuilder.Build(view).Content;
        var rail = split.Children.OfType<Border>().Single().GetLogicalDescendants().OfType<ListBox>().Single();

        // A plugin may hand its titles as a list it goes on to change. Emptying it leaves the rail with nothing
        // selected, and the host must not turn that into a request for section -1 the view would throw on.
        titles.Clear();

        Assert.Equal(-1, rail.SelectedIndex);
        Assert.Equal(new[] { 0 }, view.Shown);
    });

    [Fact]
    public void TheRailWidthTheDialogGrowsBy_IsTheOneTheThemeDrawsItAt() => HeadlessAvalonia.Run(() =>
    {
        // The dialog has to widen itself before the rail exists to be measured, so it reads the width from the same
        // token the style uses. If this key ever stops resolving, it grows by nothing and says nothing.
        Assert.True(Application.Current!.TryFindResource("CockpitSubnavRailWidth", out var width));

        Assert.Equal(184d, width);
    });

    [Fact]
    public void ADialogThatGainedARail_OpensWiderByExactlyTheRail()
    {
        var (width, minWidth) = PluginSettingsBodyBuilder.GrowForRail(640, 720, maximum: 1200, railWidth: 184);

        Assert.Equal(824, width);
        Assert.Equal(904, minWidth);
    }

    [Fact]
    public void ACockpitTooNarrowToAffordTheRail_KeepsTheDialogInsideIt()
    {
        var (width, minWidth) = PluginSettingsBodyBuilder.GrowForRail(640, 720, maximum: 658, railWidth: 184);

        Assert.Equal(658, width);
        Assert.Equal(658, minWidth);
    }

    private static Window Show(Control body)
    {
        var window = new Window { Width = 720, Height = 480, Content = body };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    // Stands in for a plugin settings control that declares sections: each is a page tall enough to scroll, and it
    // records what the host asked it to show.
    private sealed class SectionedView : UserControl, IPluginSettingsSections
    {
        private readonly Control[] _pages;

        public SectionedView(params string[] titles) : this((IReadOnlyList<string>)titles)
        {
        }

        public SectionedView(IReadOnlyList<string> titles)
        {
            SectionTitles = titles;
            _pages = [.. titles.Select(_ => TallPage())];
        }

        public List<int> Shown { get; } = [];

        public IReadOnlyList<string> SectionTitles { get; }

        public void ShowSection(int index)
        {
            Shown.Add(index);
            Content = _pages[index];
        }

        private static Control TallPage()
        {
            var page = new StackPanel { Spacing = 8 };
            for (int row = 0; row < 60; row++)
            {
                page.Children.Add(new CheckBox { Content = $"Setting row {row}" });
            }

            return page;
        }
    }
}
