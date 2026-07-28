namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The start gate (AC-345). Asserted with xunit's own Assert rather than the FluentAssertions the older files in this
/// project use: that package is commercially licensed from v8 on.
/// </summary>
public class AutopilotReadyGateTests
{
    [Fact]
    public void Decide_OnTheExecutableStage_Starts()
    {
        var decision = AutopilotReadyGate.Decide("Refuse a run that cannot be confined", "Ready", "Ready");

        Assert.True(decision.IsAllowed);
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("  Ready  ")]
    public void Decide_MatchesTheStage_IgnoringCaseAndSurroundingSpace(string reported)
    {
        Assert.True(AutopilotReadyGate.Decide("A title", reported, "Ready").IsAllowed);
    }

    [Fact]
    public void Decide_OnAnyOtherStage_RefusesAndNamesBothStages()
    {
        var decision = AutopilotReadyGate.Decide("A title", "Backlog", "Ready");

        Assert.False(decision.IsAllowed);
        Assert.Contains("Ready", decision.Reason);
        Assert.Contains("Backlog", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Decide_WithoutAReportedStage_RefusesAndSaysItCouldNotRead(string? reported)
    {
        // Fail-closed: a tracker that reports nothing has not shown the item is executable, and "cannot tell" must not
        // read as "go ahead" — that is the whole point of keying on the tracker instead of on the ticket text. The
        // wording is asserted, not just the refusal: without it this case falls through to the stage-mismatch branch,
        // which refuses too but tells the operator the item is on "" — true, and useless.
        var decision = AutopilotReadyGate.Decide("A title", reported, "Ready");

        Assert.False(decision.IsAllowed);
        Assert.Contains("could not read which stage", decision.Reason);
    }

    [Fact]
    public void Decide_WithOneOfSeveralLabelsMatching_Starts()
    {
        // A GitHub issue carries its labels, one per line — the executable one may sit anywhere among them.
        Assert.True(AutopilotReadyGate.Decide("A title", "bug\nready\nneeds design", "ready").IsAllowed);
    }

    [Fact]
    public void Decide_WithNoStageConfigured_StartsFromAnyStage()
    {
        // An operator who empties the field has turned the gate off for that tracker on purpose.
        Assert.True(AutopilotReadyGate.Decide("A title", "Backlog", string.Empty).IsAllowed);
        Assert.True(AutopilotReadyGate.Decide("A title", null, null).IsAllowed);
    }

    [Theory]
    [InlineData("[Brainstorm] Should the CEO validate on a second model?")]
    [InlineData("[brainstorm] lower case is the same marker")]
    public void Decide_OnABrainstorm_RefusesEvenOnTheExecutableStage(string title)
    {
        var decision = AutopilotReadyGate.Decide(title, "Ready", "Ready");

        Assert.False(decision.IsAllowed);
        Assert.Contains("[brainstorm]", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_OnABrainstorm_RefusesEvenWithTheGateOff()
    {
        // The marker outranks the switch: turning the stage gate off says "start from any stage", not "start an idea".
        Assert.False(AutopilotReadyGate.Decide("[Brainstorm] an idea", "Ready", string.Empty).IsAllowed);
    }

    [Fact]
    public void Decide_RefusalReason_PointsAtWhatMakesAnItemExecutable()
    {
        var decision = AutopilotReadyGate.Decide("A title", "Backlog", "Ready");

        Assert.Contains("premise still holds", decision.Reason);
        Assert.Contains("dependencies are done", decision.Reason);
    }
}
