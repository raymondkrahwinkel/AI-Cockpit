using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The managed-worktrees safety guards (AC-85): a worktree a live session is still on is never removed — that would
/// pull the working directory out from under the session — and a removal always confirms first.
/// </summary>
public class WorktreesViewModelTests
{
    [Fact]
    public async Task Remove_WorktreeWithALiveSession_DoesNothing()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var viewModel = new WorktreesViewModel(manager, Substitute.For<ISessionDialogService>());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: true));

        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_GoneWorktree_WithoutConfirmation_DoesNothing()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var viewModel = new WorktreesViewModel(manager, dialogs);

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        await manager.DidNotReceive().RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_GoneWorktree_AfterConfirmation_Removes()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var viewModel = new WorktreesViewModel(manager, dialogs);
        var row = _Row(isOwnerLive: false);

        await viewModel.RemoveCommand.ExecuteAsync(row);

        await manager.Received(1).RemoveAsync(row.Record, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive)
    {
        var record = new WorktreeRecord("session", "/repo", "/state/worktrees/ab/cockpit-x", "cockpit/x", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        return new ManagedWorktreeRowViewModel(new WorktreeStatus(record, Exists: true, HasUncommittedChanges: false, StrandableCommits: 0), isOwnerLive);
    }
}
