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
}
