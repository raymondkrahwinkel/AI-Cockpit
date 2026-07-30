using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The "Choose…" picker window (AC-502) against the real markup — the view model's own states
/// (<see cref="MemorySourceLocationPickerViewModelTests"/> in Cockpit.Core.Tests) prove the logic; this proves the
/// four states each show what they should and nothing else overlaps, and the load actually starts once the window
/// gets its view model (the real trigger — <c>OnDataContextChanged</c> — rather than calling <c>LoadAsync</c> by
/// hand, which a binding that silently didn't wire up would not catch).
/// </summary>
[Collection("avalonia")]
public class MemorySourceLocationPickerDialogTests
{
    private static ListBox LocationsList(Window window) =>
        window.GetVisualDescendants().OfType<ListBox>().Single();

    [Fact]
    public void Opening_StartsLoading_AndSettlesIntoTheListOnSuccess() => HeadlessAvalonia.Run(() =>
    {
        var location = new ProjectMemorySourceLocation("cockpit", "Cockpit", "2 documents");
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project — Synvolution", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([location])));
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        window.Close();

        Assert.False(viewModel.IsLoading, "the in-process load has already completed by the time layout settles");
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(location, Assert.Single(viewModel.Locations));
    });

    [Fact]
    public void NotSignedIn_ShowsOneSignInAction_NotAnEmptyList() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.AuthorizationRequired), signInAsync: _ => Task.FromResult(true));
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        var signInButton = window.GetVisualDescendants().OfType<Button>().First(button => ReferenceEquals(button.Command, viewModel.SignInCommand));
        window.Close();

        Assert.False(list.IsEffectivelyVisible, "not-signed-in must not read as an empty list");
        Assert.True(signInButton.IsEffectivelyVisible);
    });

    [Fact]
    public void FailedLoad_ShowsTheErrorMessage_NotAnEmptyList() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Failed("connection reset")));
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        var errorText = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Text == "connection reset");
        window.Close();

        Assert.False(list.IsEffectivelyVisible, "a failed load must not read as an empty list");
        Assert.NotNull(errorText);
    });

    [Fact]
    public void PickingALocation_EnablesChoose_AndClosingReturnsItsValue() => HeadlessAvalonia.Run(() =>
    {
        var location = new ProjectMemorySourceLocation("cockpit", "Cockpit");
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([location])));
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        var chooseButton = window.GetVisualDescendants().OfType<Button>().First(button => ReferenceEquals(button.Command, viewModel.PickCommand));
        Assert.False(chooseButton.IsEnabled, "nothing picked yet");

        list.SelectedItem = location;
        window.UpdateLayout();

        string? closedWith = "unset";
        viewModel.CloseRequested += value => closedWith = value;
        Assert.True(chooseButton.IsEnabled);
        chooseButton.Command!.Execute(null);
        window.Close();

        Assert.Equal("cockpit", closedWith);
    });

    // AC-499 review fix: the built-in ScrollIntoView scrolled the minimum distance to the pre-selected row, which
    // left the row before or after it sliced in half at whichever edge it did not stop against — a Detail line
    // with no Name above it, read as a data defect rather than a scroll position. Ten rows so the list actually
    // needs to scroll to reach the sixth; a row this test cannot see the top or bottom of proves nothing.
    [Fact]
    public void PreselectedRowNeedingScroll_LandsBothEdgesOnWholeRows() => HeadlessAvalonia.Run(() =>
    {
        var locations = Enumerable.Range(0, 10)
            .Select(i => new ProjectMemorySourceLocation($"loc{i}", $"Location {i}", $"Detail {i}"))
            .ToList();
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success(locations)), currentValue: "loc6");
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        // Not ContainerFromIndex(0): by the time the picker has scrolled to loc6, index 0 has scrolled out of view
        // and its container is exactly the kind of thing virtualization recycles — any realized row measures the
        // same uniform height (see the item DataTemplate's own remarks on TargetNullValue), so whichever one is
        // still standing works equally well.
        var someRow = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
        // Bounds excludes the item's own Margin (Theme.axaml's ListBoxItem style sets Margin="0,1"), so the true
        // distance one row advances the stack is Bounds.Height plus that margin.
        var rowHeight = someRow.Bounds.Height + someRow.Margin.Top + someRow.Margin.Bottom;
        var offsetY = scrollViewer.Offset.Y;
        var viewportHeight = scrollViewer.Viewport.Height;
        window.Close();

        Assert.True(offsetY > 0, "loc6 (index 6 of 10) needed an actual scroll — otherwise this proves nothing");
        // A row boundary at the top requires the offset itself to be a whole multiple of one row; a clean bottom
        // additionally requires the visible area to be a whole multiple too (see _ScrollToWholeRows's own remarks
        // on why both are needed, not just one).
        Assert.Equal(0, offsetY % rowHeight, precision: 3);
        Assert.Equal(0, viewportHeight % rowHeight, precision: 3);
    });

    // AC-499 review fix: the "Current" badge marks the row the operator already had when the picker opened — a
    // different concept from ListBoxItem's own :selected fill, which follows whatever was clicked most recently.
    // Confused, a click on a different row would read as "two rows are selected".
    [Fact]
    public void SelectingADifferentRow_LeavesTheCurrentBadge_OnTheOriginalRow() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new ProjectMemorySourceLocation("cockpit", "Cockpit", "2 documents");
        var olaf = new ProjectMemorySourceLocation("olaf", "Olaf", "Brain");
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([cockpit, olaf])), currentValue: "cockpit");
        var window = new MemorySourceLocationPickerDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var list = LocationsList(window);
        Assert.True(_BadgeVisible(list, cockpit), "the pre-selected row starts badged");
        Assert.False(_BadgeVisible(list, olaf));

        // The operator clicks a different row.
        list.SelectedItem = olaf;
        window.UpdateLayout();

        var cockpitStillBadged = _BadgeVisible(list, cockpit);
        var olafNowBadged = _BadgeVisible(list, olaf);
        var selectedLocation = viewModel.SelectedLocation;
        window.Close();

        Assert.Equal(olaf, selectedLocation);
        Assert.True(cockpitStillBadged, "the badge marks where the operator came from, not what was just clicked");
        Assert.False(olafNowBadged, "a newly clicked row must not read as the current one too");
    });

    private static bool _BadgeVisible(ListBox list, ProjectMemorySourceLocation location)
    {
        var container = list.ContainerFromItem(location)
            ?? throw new InvalidOperationException($"No container realized for '{location.Value}'.");
        return container.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Child is TextBlock { Text: "Current" })
            .IsVisible;
    }
}
