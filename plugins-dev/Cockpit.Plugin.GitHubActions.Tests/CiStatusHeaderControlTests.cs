using Avalonia.Media;
using Material.Icons;

namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1065 moved the header's icon/colour/"ago" logic into the shared CiRunPresentation so the new dock panel could
// reuse it. This pins the header's own pre-extraction output — same icon, same colour, same tooltip text — for a
// given run, so that move stays a refactor and never a silent behaviour change (ticket criterion 2).
public class CiStatusHeaderControlTests
{
    [Theory]
    [InlineData("completed", "success", MaterialIconKind.CheckCircleOutline, "#5AA576", "passed")]
    [InlineData("completed", "failure", MaterialIconKind.CloseCircleOutline, "#D64545", "failed")]
    [InlineData("in_progress", "", MaterialIconKind.ProgressClock, "#E0A33E", "running")]
    [InlineData("completed", "cancelled", MaterialIconKind.MinusCircleOutline, "#656c78", "cancelled")]
    public void HeaderIconColourAndText_MatchPreExtractionBehaviour(string status, string conclusion, MaterialIconKind expectedIcon, string expectedHex, string expectedStateText)
    {
        var run = new CiRun("CI", "main", "push", status, conclusion, DateTimeOffset.UtcNow.AddHours(-2), "https://github.com/o/r/actions/runs/1");

        var (icon, brush) = CiRunPresentation.Appearance(run.State);

        Assert.Equal(expectedIcon, icon);
        Assert.Equal(Color.Parse(expectedHex), Assert.IsType<SolidColorBrush>(brush).Color);
        Assert.Equal(
            $"CI: CI on 'main' — {expectedStateText} (push) · 2h ago\n\nClick to open the run on GitHub.",
            CiStatusHeaderControl.Describe(run));
    }
}
