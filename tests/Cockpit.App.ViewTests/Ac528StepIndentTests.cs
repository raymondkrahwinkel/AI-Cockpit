using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-528: at Focus, a folded run's member rows (everything under the "N steps run" anchor) must nest one step in
/// from the anchor's own line — otherwise the group's hierarchy is invisible even with it expanded, since every
/// row's tool content sat at the same fixed margin. <see cref="TranscriptEntryViewModel.IsGroupMember"/> is
/// covered at the view-model level (<c>ReadingLevelTests</c>); this checks the XAML actually renders it — a
/// binding to <c>Classes.groupMember</c> can be right in the view model and still do nothing if the style
/// selector or the wrapping element is wrong.
/// </summary>
[Collection("avalonia")]
public class Ac528StepIndentTests
{
    private static void _Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static double _AbsoluteLeft(Visual control, Visual root) =>
        control.TranslatePoint(default, root)?.X ?? throw new InvalidOperationException("control is not in the visual tree");

    private static Border _RowBorder(Visual root, TranscriptEntryViewModel entry) =>
        root.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("transcriptRow") && ReferenceEquals(b.DataContext, entry));

    // Every ToolUse row's template carries both the (usually hidden) "N steps run" button and its own tool chip;
    // GetVisualDescendants() returns hidden controls too (a hidden control keeps its last Bounds), so filtering on
    // the "toolChip" class alone can match the wrong one. IsEffectivelyVisible picks the one actually on screen.
    private static Button _ToolChipButton(Border rowBorder) =>
        rowBorder.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("toolChip") && b.IsEffectivelyVisible);

    [Fact]
    public void AGroupMember_RendersFurtherRightThanTheAnchorsOwnFoldLine() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
        // The parameterless constructor seeds a few sample rows for the previewer (see its own doc comment) —
        // clear them so the only rows in view are the two-row run this test forms, both realised without
        // needing to scroll.
        session.Transcript.Clear();
        var anchor = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "ran something")
        {
            ToolName = "Bash", ToolUseId = "t1", InputJson = "{}",
        };
        var member = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "ran something else")
        {
            ToolName = "Read", ToolUseId = "t2", InputJson = "{}",
        };
        session.Transcript.Add(anchor);
        session.Transcript.Add(member);

        var window = new Window { Width = 700, Height = 500, Content = new SessionView { DataContext = session } };
        window.Show();
        _Settle(window);

        Assert.True(anchor.IsGroupAnchor);
        Assert.True(member.IsGroupMember);

        // Expand the run so the member's own tool chip actually renders (ShowToolBlock) rather than staying folded.
        anchor.GroupToggleRequested!.Invoke();
        _Settle(window);

        var anchorFoldButton = _ToolChipButton(_RowBorder(window, anchor));
        var memberToolButton = _ToolChipButton(_RowBorder(window, member));

        var anchorLeft = _AbsoluteLeft(anchorFoldButton, window);
        var memberLeft = _AbsoluteLeft(memberToolButton, window);

        // Border.stepIndent.groupMember (Theme.axaml): Margin.Left 22 + BorderThickness.Left 1 + Padding.Left 12 =
        // 35px, measured — the border's own edge pushes the content in by one more pixel than margin+padding alone.
        // Assert the actual measured gap rather than "greater than", so a regression that nests by only a stray
        // pixel still fails loudly.
        Assert.Equal(35, memberLeft - anchorLeft, precision: 0);

        window.Close();
    });
}
