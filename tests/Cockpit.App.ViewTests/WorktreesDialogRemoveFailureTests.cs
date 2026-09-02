using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The managed-worktrees dialog says why a removal did not go through (AC-342). Raymond's report was that Remove
/// "does nothing" — it was refusing, silently, so a row that stayed put looked exactly like a broken button. This
/// measures the real dialog: the line is absent until something fails, and present, with git's words in it, after.
/// </summary>
[Collection("avalonia")]
public class WorktreesDialogRemoveFailureTests
{
    // WithNothingFailed_TheDialogShowsNoFailureLine stood here: a fresh view model, Empty(_FailureLines).
    // AfterARemovalThatLeftAFolderBehind_TheNoticeIsOnScreen makes that same assertion with a notice set — a
    // harder case for the predicate, so a failure line nobody asked for turns that test red as well.
    [Fact]
    public void AfterARefusal_TheReasonIsOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new WorktreesViewModel();
        var window = new WorktreesDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // Long enough to wrap in a 760-wide dialog, which is the layout this line has to survive: measuring a
        // wrapped run is where a message still holding newlines would allocate without end (AC-292). The view model
        // flattens it before it gets here; this proves the wrap itself completes and the text lands on screen.
        viewModel.RemoveFailure = "Could not remove 'cockpit/work-51841402' — fatal: '/home/raymond/.config/Cockpit/worktrees/ea9894eee63a/cockpit-work-51841402' is not a working tree";
        window.UpdateLayout();

        var line = Assert.Single(_FailureLines(window));
        Assert.Contains("is not a working tree", line.Text);
        Assert.True(line.Bounds.Height > 0, "an invisible explanation is the bug this fixes");
    });

    // AC-507: a removal that went through but left an unmanaged folder on disk (its repository was gone) is
    // information, not a failure — it must render, but distinct from the error-styled RemoveFailure line above.
    [Fact]
    public void AfterARemovalThatLeftAFolderBehind_TheNoticeIsOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new WorktreesViewModel();
        var window = new WorktreesDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        viewModel.RemoveNotice =
            "The repository behind 'ac-438-focus-visible' no longer exists at 'worktrees/d8bcc995e0e5/cockpit-default'. " +
            "Its worktree folder was left on disk at 'worktrees/302ed4db7cc9/ac-438-focus-visible' and is no longer managed by the cockpit.";
        window.UpdateLayout();

        var line = Assert.Single(_NoticeLines(window));
        Assert.Contains("no longer managed by the cockpit", line.Text);
        Assert.True(line.Bounds.Height > 0, "an invisible notice defeats the point of showing it");
        Assert.Empty(_FailureLines(window));
    });

    // Both can be on screen at once (a CleanUpFinished sweep that both refused one row and left another's folder
    // behind) — proves the two are actually styled apart, not just bound to different properties that happen to
    // carry the same look.
    [Fact]
    public void BothAFailureAndANotice_RenderWithDifferentColours() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new WorktreesViewModel();
        var window = new WorktreesDialog { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        viewModel.RemoveFailure = "Could not remove 'cockpit/first' — fatal: 'wt' is not a working tree";
        viewModel.RemoveNotice = "The repository behind 'cockpit/second' no longer exists and its folder was left on disk.";
        window.UpdateLayout();

        var failure = Assert.Single(_FailureLines(window));
        var notice = Assert.Single(_NoticeLines(window));
        Assert.NotEqual(((ISolidColorBrush)failure.Foreground!).Color, ((ISolidColorBrush)notice.Foreground!).Color);
    });

    private static List<TextBlock> _FailureLines(WorktreesDialog window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsVisible && block.Text?.StartsWith("Could not remove", StringComparison.Ordinal) == true)
            .ToList();

    private static List<TextBlock> _NoticeLines(WorktreesDialog window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsVisible && block.Text?.StartsWith("The repository behind", StringComparison.Ordinal) == true)
            .ToList();
}
