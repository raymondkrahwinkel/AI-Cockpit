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

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive, bool exists = true, bool workingCopyMissing = false, string branch = "cockpit/x", bool hasOpenRestoreOffer = false)
    {
        var record = new WorktreeRecord("session", "/repo", $"/state/worktrees/ab/{branch.Replace('/', '-')}", branch, "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        var status = new WorktreeStatus(record, exists, HasUncommittedChanges: false, StrandableCommits: 0) { WorkingCopyMissing = workingCopyMissing };

        return new ManagedWorktreeRowViewModel(status, isOwnerLive, hasOpenRestoreOffer: hasOpenRestoreOffer);
    }
}
