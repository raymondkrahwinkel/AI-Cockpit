using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

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

        body.HasRail.Should().BeFalse("a view that does not declare sections keeps the dialog it has today");
        body.Content.Should().BeOfType<ScrollViewer>()
            .Which.Content.Should().BeOfType<Border>()
            .Which.Child.Should().BeSameAs(view);
    });

    [Fact]
    public void ASingleSection_StaysFlat() => HeadlessAvalonia.Run(() =>
    {
        var body = PluginSettingsBodyBuilder.Build(new SectionedView("Everything"));

        body.HasRail.Should().BeFalse("a rail beside one page costs width and navigates nothing");
        body.Content.Should().BeOfType<ScrollViewer>();
    });

    [Fact]
    public void TwoSections_DrawTheRailWithTheirTitlesBesideTheView() => HeadlessAvalonia.Run(() =>
    {
        var view = new SectionedView("Run safety", "Templates");

        var body = PluginSettingsBodyBuilder.Build(view);

        body.HasRail.Should().BeTrue();
        var split = body.Content.Should().BeOfType<Grid>().Subject;
        var rail = split.Children.OfType<Border>().Single();
        rail.Classes.Should().Contain("subnavRail", "the rail reuses the Options styles rather than a second visual language");

        rail.GetLogicalDescendants().OfType<TextBlock>().Single().Text.Should().Be("SETTINGS");

        var items = rail.GetLogicalDescendants().OfType<ListBox>().Single();
        items.Classes.Should().Contain("subnav");
        items.ItemsSource.Should().BeEquivalentTo(new[] { "Run safety", "Templates" }, options => options.WithStrictOrdering());

        // The view is still the scrolled content: it stays attached for the whole dialog, so a settings view that
        // loads on attach or unsubscribes on detach behaves exactly as it does without a rail.
        split.Children.OfType<ScrollViewer>().Single()
            .Content.Should().BeOfType<Border>()
            .Which.Child.Should().BeSameAs(view);
    });

    [Fact]
    public void TheDialogOpensOnTheFirstSection() => HeadlessAvalonia.Run(() =>
    {
        var view = new SectionedView("Run safety", "Templates");

        PluginSettingsBodyBuilder.Build(view);

        view.Shown.Should().Equal(0);
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
        scroll.Offset.Y.Should().BeGreaterThan(0, "the first section has to be scrolled for the reset to mean anything");

        split.Children.OfType<Border>().Single().GetLogicalDescendants().OfType<ListBox>().Single().SelectedIndex = 1;
        window.UpdateLayout();

        view.Shown.Should().Equal(0, 1);
        scroll.Offset.Y.Should().Be(0, "a section opens at its top, not where the previous one was left");

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

        rail.SelectedIndex.Should().Be(-1, "the rail has nothing left to select");
        view.Shown.Should().Equal(0);
    });

    [Fact]
    public void TheRailWidthTheDialogGrowsBy_IsTheOneTheThemeDrawsItAt() => HeadlessAvalonia.Run(() =>
    {
        // The dialog has to widen itself before the rail exists to be measured, so it reads the width from the same
        // token the style uses. If this key ever stops resolving, it grows by nothing and says nothing.
        Application.Current!.TryFindResource("CockpitSubnavRailWidth", out var width).Should().BeTrue();

        width.Should().Be(184d);
    });

    [Fact]
    public void ADialogThatGainedARail_OpensWiderByExactlyTheRail()
    {
        var (width, minWidth) = PluginSettingsBodyBuilder.GrowForRail(640, 720, maximum: 1200, railWidth: 184);

        width.Should().Be(824);
        minWidth.Should().Be(904, "the settings column keeps the 720 it had, and the rail is added to it");
    }

    [Fact]
    public void ACockpitTooNarrowToAffordTheRail_KeepsTheDialogInsideIt()
    {
        var (width, minWidth) = PluginSettingsBodyBuilder.GrowForRail(640, 720, maximum: 658, railWidth: 184);

        width.Should().Be(658, "a dialog wider than the window behind it opens with its content cut off");
        minWidth.Should().Be(658);
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
