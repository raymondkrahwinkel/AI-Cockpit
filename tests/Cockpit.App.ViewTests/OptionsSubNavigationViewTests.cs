using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-69: the Options redesign keeps the top tab-bar but splits a tab into left-rail sub-pages. Voice is the
/// fully-worked example — Transcribe · Assistant, one page at a time — and the rail drives which page shows. This
/// pins that wiring: a XAML rename of the sub-nav or its element-name binding to the Carousel would otherwise only
/// surface by opening the dialog and clicking, which no unit test does.
/// </summary>
/// <remarks>
/// AC-546 follow-up: the Voice tab used to carry a third page, "Read-aloud", whose settings (voice, language,
/// barge-in) moved onto the Assistant page once sessions stopped reading their own replies aloud — a page named
/// after a removed feature is itself a trace of that feature (ticket criterion 5). The rail dropped from three
/// items to two; this test's own indices moved with it.
/// </remarks>
[Collection("avalonia")]
public class OptionsSubNavigationViewTests
{
    [Fact]
    public void TheVoiceTab_SplitsIntoTwoSubPages_TheRailDrivesWhichShows() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        // The Voice tab's content is only realised once it is the selected tab, so select it and force a layout
        // pass before reaching into its rail and Carousel.
        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Header as string == "Voice");
        dialog.UpdateLayout();

        var rail = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "VoiceNav");
        // "Assistant" is AC-543's, and last: the page before it is about the microphone, and this one is about a
        // feature that can be used with neither (and, since AC-546, also carries the speaker settings that used
        // to be their own "Read-aloud" page).
        Assert.Equal(
            new[] { "Transcribe", "Assistant" },
            rail.Items.OfType<ListBoxItem>().Select(item => item.Content as string));

        var carousel = dialog.GetVisualDescendants().OfType<Carousel>().Single();
        Assert.Equal(0, rail.SelectedIndex);
        Assert.Equal(0, carousel.SelectedIndex);

        // The last page, so the rail and the carousel are held to agreeing all the way to the end rather than
        // only where they happened to line up before a page was added.
        rail.SelectedIndex = 1;
        Assert.Equal(1, carousel.SelectedIndex);

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

            Assert.True(
                dialog.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("subnavRail")),
                $"the {tab.Header} tab is expected to render its sub-nav rail");
        }

        dialog.Close();
    });
}
