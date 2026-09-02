namespace Cockpit.Plugin.Autopilot.Tests;

// The start gate (AC-345). Asserted with xunit's own Assert rather than the FluentAssertions the older files in this
// project use: that package is commercially licensed from v8 on.
public class AutopilotReadyGateTests
{
    [Theory]
    // The plain case: the item sits on the stage the operator configured.
    [InlineData("Refuse a run that cannot be confined", "Ready", "Ready")]
    // Stage matching ignores case and surrounding space, because trackers report neither consistently.
    [InlineData("A title", "ready", "Ready")]
    [InlineData("A title", "  Ready  ", "Ready")]
    // A GitHub issue carries its labels, one per line — the executable one may sit anywhere among them.
    [InlineData("A title", "bug\nready\nneeds design", "ready")]
    // An operator who empties the field has turned the gate off for that tracker on purpose.
    [InlineData("A title", "Backlog", "")]
    [InlineData("A title", null, null)]
    public void Decide_OnAnItemTheGateLetsThrough_StartsWithNothingToSay(string title, string? reported, string? configured)
    {
        var decision = AutopilotReadyGate.Decide(title, reported, configured);

        Assert.True(decision.IsAllowed);
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Fact]
    public void Decide_OnAnyOtherStage_RefusesAndNamesBothStages_AndWhatWouldMakeItExecutable()
    {
        var decision = AutopilotReadyGate.Decide("A title", "Backlog", "Ready");

        Assert.False(decision.IsAllowed);
        Assert.Contains("Ready", decision.Reason);
        Assert.Contains("Backlog", decision.Reason);
        Assert.Contains("premise still holds", decision.Reason);
        Assert.Contains("dependencies are done", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Decide_WithoutAReportedStage_RefusesAndSaysItCouldNotRead(string? reported)
    {
        // Fail-closed: a tracker that reports nothing has not shown the item is executable, and "cannot tell" must
        // not read as "go ahead". The wording is asserted, not just the refusal: without it this falls through to
        // the stage-mismatch branch, which refuses too but tells the operator the item is on "" — true, and useless.
        var decision = AutopilotReadyGate.Decide("A title", reported, "Ready");

        Assert.False(decision.IsAllowed);
        Assert.Contains("could not read which stage", decision.Reason);
    }

    [Theory]
    [InlineData("[Brainstorm] Should the CEO validate on a second model?", "Ready")]
    [InlineData("[brainstorm] lower case is the same marker", "Ready")]
    // The marker outranks the switch: turning the stage gate off says "start from any stage", not "start an idea".
    [InlineData("[Brainstorm] an idea", "")]
    public void Decide_OnABrainstorm_Refuses_WhateverTheStageGateSays(string title, string configured)
    {
        var decision = AutopilotReadyGate.Decide(title, "Ready", configured);

        Assert.False(decision.IsAllowed);
        Assert.Contains("[brainstorm]", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
