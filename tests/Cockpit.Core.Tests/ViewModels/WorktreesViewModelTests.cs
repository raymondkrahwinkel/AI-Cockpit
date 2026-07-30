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

    [Fact]
    public async Task Remove_TheManagerRefuses_SaysWhyRatherThanLookingLikeNothingHappened()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        Assert.Contains("is not a working tree", viewModel.RemoveFailure);
        Assert.True(viewModel.HasRemoveFailure);
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

        Assert.DoesNotContain("\n", viewModel.RemoveFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", viewModel.RemoveFailure, StringComparison.Ordinal);
        Assert.Contains("containing submodules", viewModel.RemoveFailure);
    }

    [Fact]
    public async Task Remove_AfterARefusal_ThatSucceeds_ClearsTheMessage()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        Assert.Null(viewModel.RemoveFailure);
        Assert.False(viewModel.HasRemoveFailure);
    }

    [Fact]
    public async Task Remove_SucceedsButLeavesTheFolderBehind_ShowsTheNoticeRatherThanTheErrorStyling()
    {
        // AC-507: a removal that went through — the entry is gone — but left an unmanaged folder on disk because its
        // repository disappeared. That is information, not a failure, so it must land on RemoveNotice, never on
        // RemoveFailure (which reads as an error in the dialog).
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("The repository behind 'cockpit/x' no longer exists. Its worktree folder was left on disk.");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        Assert.True(viewModel.HasRemoveNotice);
        Assert.Contains("left on disk", viewModel.RemoveNotice);
        Assert.Null(viewModel.RemoveFailure);
        Assert.False(viewModel.HasRemoveFailure);
    }

    [Fact]
    public async Task Remove_PlainSuccess_HasNoNotice()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());

        await viewModel.RemoveCommand.ExecuteAsync(_Row(isOwnerLive: false));

        Assert.Null(viewModel.RemoveNotice);
        Assert.False(viewModel.HasRemoveNotice);
    }

    [Fact]
    public async Task CleanUpFinished_OneThatWillNotGo_IsNamedRatherThanSilentlySkipped()
    {
        var manager = _RefusingManager("fatal: 'wt' is not a working tree");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        viewModel.Worktrees.Add(_Row(isOwnerLive: false));

        await viewModel.CleanUpFinishedCommand.ExecuteAsync(null);

        Assert.Contains("cockpit/x", viewModel.RemoveFailure);
        Assert.Contains("is not a working tree", viewModel.RemoveFailure);
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

    [Fact]
    public async Task CleanUpFinished_AWorktreeWhoseFolderIsThereButHoldsNoCheckout_IsSweptToo()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        var shell = _Row(isOwnerLive: false, workingCopyMissing: true);
        viewModel.Worktrees.Add(shell);

        await viewModel.CleanUpFinishedCommand.ExecuteAsync(null);

        // The folder is still on disk, so the "gone folder" clause above does not catch it — but there is no working
        // copy in it to lose, which is the thing the sweep actually asks about.
        await manager.Received(1).RemoveAsync(shell.Record, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanUpFinished_TwoLeaveNoticesBehind_BothLandOnTheAggregatedNotice()
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        var first = _Row(isOwnerLive: false, branch: "cockpit/first");
        var second = _Row(isOwnerLive: false, branch: "cockpit/second");
        manager.RemoveAsync(first.Record, false, Arg.Any<CancellationToken>()).Returns("left first's folder behind");
        manager.RemoveAsync(second.Record, false, Arg.Any<CancellationToken>()).Returns("left second's folder behind");
        var viewModel = new WorktreesViewModel(manager, _ConfirmingDialogs());
        viewModel.Worktrees.Add(first);
        viewModel.Worktrees.Add(second);

        await viewModel.CleanUpFinishedCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasRemoveNotice);
        Assert.Contains("left first's folder behind", viewModel.RemoveNotice);
        Assert.Contains("left second's folder behind", viewModel.RemoveNotice);
    }

    private static IWorktreeManager _RefusingManager(string message)
    {
        var manager = Substitute.For<IWorktreeManager>();
        manager.GetStatusesAsync(Arg.Any<CancellationToken>()).Returns([]);
        manager.RemoveAsync(Arg.Any<WorktreeRecord>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException(message)));

        return manager;
    }

    private static ISessionDialogService _ConfirmingDialogs()
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        return dialogs;
    }

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive, bool exists = true, bool workingCopyMissing = false, string branch = "cockpit/x")
    {
        var record = new WorktreeRecord("session", "/repo", $"/state/worktrees/ab/{branch.Replace('/', '-')}", branch, "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        var status = new WorktreeStatus(record, exists, HasUncommittedChanges: false, StrandableCommits: 0) { WorkingCopyMissing = workingCopyMissing };

        return new ManagedWorktreeRowViewModel(status, isOwnerLive);
    }
}
