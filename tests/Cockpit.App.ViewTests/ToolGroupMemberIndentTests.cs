using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-554: an expanded run's chips must read as nested under the "N steps run" line — the button stays on the
/// outer margin, but every chip in the run (anchor's own included) sits one level in behind a hairline guide.
/// A tool row that is not in any run stays flush, unchanged. Pinned here because the indent is style-driven
/// (a Classes.grouped binding), which a future refactor could silently drop without a compile error.
/// </summary>
[Collection("avalonia")]
public class ToolGroupMemberIndentTests
{
    [Fact]
    public void ExpandedRun_IndentsEveryMemberChip_StandaloneRowStaysFlush() => HeadlessAvalonia.Run(() =>
    {
        // Two consecutive auto tool calls form a run (SessionViewModel._RecomputeReadingGroups groups any run of
        // 2+), a plain text row breaks it, and a lone auto tool call after that stays ungrouped — exactly the
        // "run vs. standalone" split the acceptance criteria draw.
        var anchor = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "anchor") { ToolName = "Read" };
        var member = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "member") { ToolName = "ToolSearch" };
        var spacer = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "in between");
        var standalone = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "standalone") { ToolName = "Bash" };

        var session = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
        session.Transcript.Add(anchor);
        session.Transcript.Add(member);
        session.Transcript.Add(spacer);
        session.Transcript.Add(standalone);

        anchor.GroupToggleRequested!.Invoke();

        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new SessionView { DataContext = session },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The design-time SessionViewModel() ctor seeds sample transcript rows for the Avalonia previewer
        // (Screenshotter etc.) alongside the three under test here, so borders are matched by DataContext
        // identity rather than by position or count.
        var groupBorders = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("toolGroupMember"))
            .ToDictionary(border => (TranscriptEntryViewModel)border.DataContext!, border => border);

        var anchorBorder = groupBorders[anchor];
        var memberBorder = groupBorders[member];
        var standaloneBorder = groupBorders[standalone];

        // Read every styled value before closing the window: Avalonia clears style-applied local values on
        // detach from the visual tree, so a Border queried after Close() reports its unstyled defaults.
        Assert.Contains("grouped", anchorBorder.Classes);
        Assert.Contains("grouped", memberBorder.Classes);
        Assert.DoesNotContain("grouped", standaloneBorder.Classes);

        Assert.True(anchorBorder.BorderThickness.Left > 0, "the anchor's own chip must nest in behind the guide too");
        Assert.True(memberBorder.BorderThickness.Left > 0);
        Assert.Equal(0, standaloneBorder.BorderThickness.Left);

        Assert.True(anchorBorder.Padding.Left > 0);
        Assert.Equal(anchorBorder.Padding.Left, memberBorder.Padding.Left);
        Assert.Equal(0, standaloneBorder.Padding.Left);

        window.Close();
    });
}
