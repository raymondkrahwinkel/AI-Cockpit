using Avalonia.Controls;
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
    [Fact]
    public void WithNothingFailed_TheDialogShowsNoFailureLine() => HeadlessAvalonia.Run(() =>
    {
        var window = new WorktreesDialog { DataContext = new WorktreesViewModel() };
        window.Show();
        window.UpdateLayout();

        Assert.Empty(_FailureLines(window));
    });

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

    private static List<TextBlock> _FailureLines(WorktreesDialog window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsVisible && block.Text?.StartsWith("Could not remove", StringComparison.Ordinal) == true)
            .ToList();
}
