using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="MemorySourceLocationPickerViewModel"/> (AC-502) — the "Choose…" picker's own state machine, covering
/// its four criteria without standing up a window: a loaded list, "not signed in" as one action rather than an
/// empty list (criterion 4), a failed load says what went wrong rather than showing nothing (criterion 5), and
/// picking/cancelling closes with the right value.
/// </summary>
public class MemorySourceLocationPickerViewModelTests
{
    private static readonly ProjectMemorySourceLocation Cockpit = new("cockpit", "Cockpit", "2 documents");

    [Fact]
    public async Task LoadAsync_Success_PopulatesLocationsAndClearsTheOtherStates()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([Cockpit])));

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.NeedsSignIn);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(Cockpit, Assert.Single(viewModel.Locations));
    }

    [Fact]
    public async Task LoadAsync_EmptySuccess_IsNotConfusedWithAuthorizationRequiredOrFailed()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([])));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Locations);
        Assert.False(viewModel.NeedsSignIn);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_AuthorizationRequired_SetsNeedsSignIn_NeverAnEmptyListDisguisedAsSuccess()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.AuthorizationRequired));

        await viewModel.LoadAsync();

        Assert.True(viewModel.NeedsSignIn);
        Assert.Empty(viewModel.Locations);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_Failed_SetsErrorMessage_NeverAnEmptyListDisguisedAsSuccess()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Failed("connection reset")));

        await viewModel.LoadAsync();

        Assert.Equal("connection reset", viewModel.ErrorMessage);
        Assert.Empty(viewModel.Locations);
        Assert.False(viewModel.NeedsSignIn);
    }

    [Fact]
    public async Task LoadAsync_TheListingDelegateThrows_SetsErrorMessage_RatherThanPropagating()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => throw new InvalidOperationException("boom"));

        await viewModel.LoadAsync();

        Assert.Equal("boom", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_AnOlderCallResolvingAfterANewerOne_IsDiscarded_NeverOverwritesTheNewerResult()
    {
        // Review fix: LoadAsync has no re-entrancy guard of its own, so an in-flight call (started, say, by the
        // window opening) can still be running when Retry/SignIn starts a second one. Whichever answers second
        // must win — an older, slower answer landing later must not stomp the newer one, breaking the "the states
        // are mutually exclusive" invariant.
        var firstGate = new TaskCompletionSource<ProjectMemorySourceLocationsResult>();
        var callCount = 0;
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project",
            _ =>
            {
                callCount++;
                // The first call blocks until released below; the second resolves immediately, so it is free to
                // finish (and set state) before the first one's await ever returns.
                return callCount == 1 ? firstGate.Task : Task.FromResult(ProjectMemorySourceLocationsResult.Success([Cockpit]));
            });

        var firstLoad = viewModel.LoadAsync();
        var secondLoad = viewModel.LoadAsync();
        await secondLoad;
        Assert.Equal(Cockpit, Assert.Single(viewModel.Locations));

        // The first (older) call now resolves with a different, stale answer — it must be discarded rather than
        // overwrite what the second (newer, already-applied) call decided.
        firstGate.SetResult(ProjectMemorySourceLocationsResult.Failed("stale error"));
        await firstLoad;

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(Cockpit, Assert.Single(viewModel.Locations));
    }

    [Fact]
    public async Task SignInCommand_Succeeds_ReloadsTheList()
    {
        var attempt = 0;
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project",
            _ => Task.FromResult(attempt++ == 0 ? ProjectMemorySourceLocationsResult.AuthorizationRequired : ProjectMemorySourceLocationsResult.Success([Cockpit])),
            signInAsync: _ => Task.FromResult(true));
        await viewModel.LoadAsync();
        Assert.True(viewModel.NeedsSignIn);

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.False(viewModel.NeedsSignIn);
        Assert.Equal(Cockpit, Assert.Single(viewModel.Locations));
    }

    [Fact]
    public async Task SignInCommand_Declines_StaysInNeedsSignIn_WithoutReloading()
    {
        var listCalls = 0;
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project",
            _ => { listCalls++; return Task.FromResult(ProjectMemorySourceLocationsResult.AuthorizationRequired); },
            signInAsync: _ => Task.FromResult(false));
        await viewModel.LoadAsync();
        Assert.Equal(1, listCalls);

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.True(viewModel.NeedsSignIn);
        Assert.Equal(1, listCalls);
    }

    [Fact]
    public void CanSignIn_NoSignInDelegate_IsFalse()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.AuthorizationRequired));

        Assert.False(viewModel.CanSignIn);
    }

    [Fact]
    public async Task RetryCommand_RunsTheListingDelegateAgain()
    {
        var attempt = 0;
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project",
            _ => Task.FromResult(attempt++ == 0 ? ProjectMemorySourceLocationsResult.Failed("first try") : ProjectMemorySourceLocationsResult.Success([Cockpit])));
        await viewModel.LoadAsync();
        Assert.Equal("first try", viewModel.ErrorMessage);

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(Cockpit, Assert.Single(viewModel.Locations));
    }

    [Fact]
    public async Task PickCommand_ClosesWithTheSelectedLocationsValue()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([Cockpit])));
        await viewModel.LoadAsync();
        viewModel.SelectedLocation = viewModel.Locations.Single();
        string? closedWith = "unset";
        viewModel.CloseRequested += value => closedWith = value;

        viewModel.PickCommand.Execute(null);

        Assert.Equal("cockpit", closedWith);
    }

    [Fact]
    public async Task PickCommand_NothingSelected_CannotExecute()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([Cockpit])));
        await viewModel.LoadAsync();

        Assert.False(viewModel.PickCommand.CanExecute(null));
    }

    [Fact]
    public void CancelCommand_ClosesWithNull()
    {
        var viewModel = new MemorySourceLocationPickerViewModel(
            "Depot project", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([])));
        string? closedWith = "unset";
        viewModel.CloseRequested += value => closedWith = value;

        viewModel.CancelCommand.Execute(null);

        Assert.Null(closedWith);
    }
}
