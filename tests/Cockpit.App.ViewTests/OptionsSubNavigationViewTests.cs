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
/// AC-1001 added Profiles, the 13th category, under WORKING right after Sessions. AC-1002 added MCP Servers, the
/// 14th, under SYSTEM right after Security.
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
        "security", "mcp-servers", "nodes", "backup", "updates", "debug",
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
    // AC-1019: a category page's root used to always be a ScrollViewer — Profiles now roots on a Grid instead,
    // so its list and detail columns can scroll independently rather than sharing one page-wide scroller. Matched
    // by Control rather than ScrollViewer so the switch itself, not one particular root element type, is pinned.
    [Fact]
    public void SelectingACategory_ShowsItsPage_AndHidesTheOthers() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();
        dialog.UpdateLayout();

        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        var content = dialog.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "CategoryContent");

        // Scoped to CategoryContent's direct children rather than every dialog descendant: the sidebar's own
        // ListBoxItems carry the same Tag values, and matching by Control anywhere would collide with them.
        var pages = content.Children.OfType<Control>()
            .Where(control => control.Tag is string tag && ExpectedCategoryTags.Contains(tag))
            .ToDictionary(control => (string)control.Tag!);

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
