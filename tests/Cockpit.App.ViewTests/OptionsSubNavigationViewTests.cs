using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1000: the eight-tab TabControl (AC-69's per-tab sub-nav rail among them) was replaced by one searchable
/// sidebar — a single "CategoryNav" ListBox holding selectable categories and 3 non-selectable group headers,
/// each category's content its own always-present ScrollViewer switched on by a Tag comparison. This pins that
/// wiring: a XAML rename of the nav, a category dropped from the list, or a group header that became selectable
/// would otherwise only surface by opening the dialog and clicking, which no unit test does.
/// AC-1001 added Profiles, the 13th category, under WORKING right after Sessions.
/// </summary>
/// <remarks>
/// Replaces the AC-69-era version of this file, whose two tests (a Voice tab that split into a
/// Transcribe/Assistant Carousel, and "every tab hangs under its own sub-nav rail") no longer describe anything
/// this dialog does — Assistant is its own top-level category now, not a Voice sub-page, and there is one main
/// sidebar rather than eight per-tab rails.
/// </remarks>
[Collection("avalonia")]
public class OptionsSubNavigationViewTests
{
    // WORKING, VOICE & ASSISTANT, SYSTEM, in this order — matches AC-1000's acceptance criterion 1, plus
    // Profiles (AC-1001) right after Sessions.
    private static readonly string[] ExpectedCategoryTags =
    [
        "sessions", "profiles", "appearance", "terminal", "notifications", "shortcuts",
        "voice", "assistant",
        "security", "nodes", "backup", "updates", "debug",
    ];

    [Fact]
    public void TheSidebar_ListsAllCategories_InTheGroomedOrder() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var tags = nav.Items.OfType<ListBoxItem>()
            .Select(item => item.Tag as string)
            .Where(tag => tag is not null)
            .ToArray();

        Assert.Equal(ExpectedCategoryTags, tags);

        dialog.Close();
    });

    // AC5: group headers are not selectable and not focusable with Tab.
    [Fact]
    public void GroupHeaders_AreNotSelectable_AndNotFocusable() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var headers = nav.Items.OfType<ListBoxItem>().Where(item => item.Classes.Contains("navGroupHeader")).ToList();

        Assert.Equal(3, headers.Count);
        Assert.All(headers, header =>
        {
            Assert.False(header.Focusable, "a group header must not be reachable with Tab");
            Assert.False(header.IsHitTestVisible, "a group header must not be selectable by clicking it");
        });

        dialog.Close();
    });

    // Proves the Tag-based content switch actually wires up at runtime (CategoryTagEqualsConverter), not just that
    // the markup for both category pages exists.
    [Fact]
    public void SelectingACategory_ShowsItsPage_AndHidesTheOthers() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();
        dialog.UpdateLayout();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var pages = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(sv => sv.Tag is string tag && ExpectedCategoryTags.Contains(tag))
            .ToDictionary(sv => (string)sv.Tag!);

        Assert.Equal(ExpectedCategoryTags.ToHashSet(), pages.Keys.ToHashSet());

        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "debug");
        dialog.UpdateLayout();

        Assert.True(pages["debug"].IsEffectivelyVisible);
        Assert.All(
            ExpectedCategoryTags.Where(tag => tag != "debug"),
            tag => Assert.False(pages[tag].IsEffectivelyVisible, $"'{tag}' should be hidden while 'debug' is selected"));

        dialog.Close();
    });
}
