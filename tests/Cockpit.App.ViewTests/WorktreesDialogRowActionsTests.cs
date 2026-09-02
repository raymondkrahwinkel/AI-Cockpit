using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Worktrees;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The row actions in the managed-worktrees dialog (AC-520). Release is the one action that is hidden rather than
/// disabled — on almost every row, releasing is not a question anyone is asking, and a permanently greyed button
/// would say otherwise. Its neighbours stay visible while disabled because their tooltip explains the refusal.
/// These measure the rendered dialog: a binding that silently went back to IsEnabled would leave the button on
/// screen, which no view-model test can see.
/// </summary>
[Collection("avalonia")]
public class WorktreesDialogRowActionsTests
{
    [Fact]
    public void ARowThatCannotBeReleased_DoesNotShowTheReleaseButton() => HeadlessAvalonia.Run(() =>
    {
        var window = _DialogShowing(_Row(isOwnerLive: true, hasOpenRestoreOffer: false));

        Assert.DoesNotContain(_RowActions(window), button => _NameOf(button) == "Release" && button.IsVisible);
    });

    /// <summary>
    /// Release comes first because it is what unlocks the other two — checked on the rendered order rather than the
    /// markup, so a later edit that appends it back at the end fails here.
    /// </summary>
    [Fact]
    public void ARowLiveOnlyBecauseOfARestoreOffer_OffersReleaseAndOffersItFirst() => HeadlessAvalonia.Run(() =>
    {
        var window = _DialogShowing(_Row(isOwnerLive: true, hasOpenRestoreOffer: true));

        var release = Assert.Single(_RowActions(window), button => _NameOf(button) == "Release");
        Assert.True(release.IsVisible, "the one row where releasing applies is the row that must offer it");

        var visible = _RowActions(window).Where(button => button.IsVisible).ToList();
        Assert.Equal("Release", _NameOf(visible[0]));
    });

    /// <summary>
    /// Every action carries an icon and no text (Raymond, AC-520) — and therefore has to carry its name somewhere a
    /// screen reader reaches, since the label it used to have is gone.
    /// </summary>
    [Fact]
    public void EveryAction_IsAnIconWithAnAccessibleName() => HeadlessAvalonia.Run(() =>
    {
        var window = _DialogShowing(_Row(isOwnerLive: false, hasOpenRestoreOffer: false));

        var actions = _RowActions(window);
        Assert.NotEmpty(actions);
        foreach (var button in actions)
        {
            Assert.IsType<MaterialIcon>(button.Content);
            Assert.False(string.IsNullOrWhiteSpace(_NameOf(button)), "an icon-only button with no name announces itself as nothing");
        }
    });

    private static WorktreesDialog _DialogShowing(ManagedWorktreeRowViewModel row)
    {
        var viewModel = new WorktreesViewModel();
        viewModel.Worktrees.Clear();
        viewModel.Worktrees.Add(row);

        var window = new WorktreesDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static List<Button> _RowActions(WorktreesDialog window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("RowAction"))
            .ToList();

    private static string? _NameOf(Button button) => Avalonia.Automation.AutomationProperties.GetName(button);

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive, bool hasOpenRestoreOffer)
    {
        var record = new WorktreeRecord("session", "/repo", "/state/worktrees/ab/cockpit-x", "cockpit/x", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        var status = new WorktreeStatus(record, Exists: true, HasUncommittedChanges: false, StrandableCommits: 0);

        return new ManagedWorktreeRowViewModel(status, isOwnerLive, "AC-520", hasOpenRestoreOffer);
    }
}
