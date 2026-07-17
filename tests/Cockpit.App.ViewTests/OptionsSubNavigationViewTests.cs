using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-69: the Options redesign keeps the top tab-bar but splits a tab into left-rail sub-pages. Voice is the
/// fully-worked example — Read-aloud · Transcribe · Cleanup, one page at a time — and the rail drives which page
/// shows. This pins that wiring: a XAML rename of the sub-nav or its element-name binding to the Carousel would
/// otherwise only surface by opening the dialog and clicking, which no unit test does.
/// </summary>
[Collection("avalonia")]
public class OptionsSubNavigationViewTests
{
    [Fact]
    public void TheVoiceTab_SplitsIntoThreeSubPages_TheRailDrivesWhichShows() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        // The Voice tab's content is only realised once it is the selected tab, so select it and force a layout
        // pass before reaching into its rail and Carousel.
        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Header as string == "Voice");
        dialog.UpdateLayout();

        var rail = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "VoiceNav");
        rail.Items.OfType<ListBoxItem>().Select(item => item.Content as string)
            .Should().Equal("Read-aloud", "Transcribe", "Cleanup");

        var carousel = dialog.GetVisualDescendants().OfType<Carousel>().Single();
        rail.SelectedIndex.Should().Be(0, "the rail opens on the first sub-page");
        carousel.SelectedIndex.Should().Be(0, "the Carousel starts on the page the rail has selected");

        rail.SelectedIndex = 2;
        carousel.SelectedIndex.Should().Be(2, "the shown page follows the rail selection, not a separate state");

        dialog.Close();
    });

    // The single-page tabs still hang under the new rail (AC-69 umbrella): each is a Grid split into the rail
    // column and a detail ScrollViewer, so a later ticket can add rail items without a structural change.
    [Fact]
    public void EveryOptionsTab_HangsUnderASubNavRail() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = tab;
            dialog.UpdateLayout();

            dialog.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("subnavRail"))
                .Should().BeTrue($"the {tab.Header} tab is expected to render its sub-nav rail");
        }

        dialog.Close();
    });
}
