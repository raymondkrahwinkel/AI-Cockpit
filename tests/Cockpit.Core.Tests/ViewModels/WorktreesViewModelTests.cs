using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using FluentAssertions;
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

    [Fact]
    public async Task Remove_TheManagerRefuses_SaysWhyRatherThanLookingLikeNothingHappened()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        viewModel.RemoveFailure.Should().Contain("is not a working tree");
        viewModel.HasRemoveFailure.Should().BeTrue();
    }

    /// <summary>
    /// git says why across several lines. The dialog wraps its message, and a wrapping TextBlock over text holding
    /// newlines is what took the prompt preview down with an OutOfMemoryException on Avalonia 12.0.5 (AC-292).
    /// </summary>
    [Fact]
    public async Task Remove_AMultiLineRefusal_IsFlattenedToOneLine()
    {
        var manager = _RefusingManager("fatal: could not remove\nworking trees containing submodules\ncannot be moved");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        viewModel.RemoveFailure.Should().NotContainAny("\n", "\r").And.Contain("containing submodules");
    }

    [Fact]
    public async Task Remove_AfterARefusal_ThatSucceeds_ClearsTheMessage()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        viewModel.RemoveFailure.Should().BeNull();
        viewModel.HasRemoveFailure.Should().BeFalse();
    }

    [Fact]
    public async Task CleanUpFinished_OneThatWillNotGo_IsNamedRatherThanSilentlySkipped()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        viewModel.Worktrees.Add(_Row(isOwnerLive: false));

        await viewModel.CleanUpFinishedCommand.ExecuteAsync(null);

        viewModel.RemoveFailure.Should().Contain("cockpit/x").And.Contain("is not a working tree");
    }

    [Fact]
    public async Task CleanUpFinished_AWorktreeWhoseFolderIsGone_IsSweptToo()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        var gone = _Row(isOwnerLive: false, exists: false);
        viewModel.Worktrees.Add(gone);

        await viewModel.CleanUpFinishedCommand.ExecuteAsync(null);

        // It does not read as clean (nothing about a missing folder can be measured), but there is no working copy
        // left to lose and the branch survives the removal — so the sweep is exactly where it belongs.
        await manager.Received(1).RemoveAsync(gone.Record, false, Arg.Any<CancellationToken>());
    }

    private static IWorktreeManager _RefusingManager(string message)
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(message)));

        return manager;
    }

    private static ISessionDialogService _ConfirmingDialogs()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        return dialogs;
    }

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive, bool exists = true)
    {
        var record = new WorktreeRecord("session", "/repo", "/state/worktrees/ab/cockpit-x", "cockpit/x", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        return new ManagedWorktreeRowViewModel(new WorktreeStatus(record, exists, HasUncommittedChanges: false, StrandableCommits: 0), isOwnerLive);
    }
}
