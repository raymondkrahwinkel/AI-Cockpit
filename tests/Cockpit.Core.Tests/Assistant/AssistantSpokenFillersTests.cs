using Cockpit.Core.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The lines the cockpit speaks when the model said nothing (AC-597) and while it keeps working (AC-598).
/// </summary>
public class AssistantSpokenFillersTests
{
    [Theory]
    [InlineData("nl")]
    [InlineData("NL")]
    [InlineData("en")]
    public void EachLanguageHasWords_AndTheyDoNotRepeatBackToBack(string language)
    {
        var spoken = Enumerable.Range(0, 4)
            .Select(turn => AssistantSpokenFillers.GoingToLookUpSomething(language, turn))
            .ToArray();

        Assert.All(spoken, line => Assert.NotEqual(string.Empty, line));
        Assert.Equal(spoken.Length, spoken.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ALanguageWeHaveNoWordsIn_StaysSilent_RatherThanAnsweringInTheWrongOne()
    {
        // The assistant answers in the language it was spoken to. A filler is the one sentence the model did not
        // produce, so it is also the one that could arrive in the wrong language — silence is the better failure.
        Assert.Equal(string.Empty, AssistantSpokenFillers.GoingToLookUpSomething("de", 0));
        Assert.Equal(string.Empty, AssistantSpokenFillers.StillAtIt("fr", 0));
        Assert.Equal(string.Empty, AssistantSpokenFillers.GoingToLookUpSomething(null, 0));
    }

    [Fact]
    public void TheRotationKeepsWorking_PastTheEndOfTheList_AndOnALongRunningTurnCounter()
    {
        // The counter is never reset per turn, so it climbs for as long as the cockpit is open. An index that
        // wrapped into a negative would throw here rather than in a test, and the operator would hear the crash.
        Assert.NotEqual(string.Empty, AssistantSpokenFillers.GoingToLookUpSomething("nl", 9_999));
        Assert.NotEqual(string.Empty, AssistantSpokenFillers.StillAtIt("en", int.MaxValue));
    }

    [Fact]
    public void TheSignOfLifeWidens_AndThenStopsWidening()
    {
        var delays = Enumerable.Range(0, 8).Select(AssistantSpokenFillers.SignOfLifeDelay).ToArray();

        Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);

        // Each wait is longer than the last until the cap: a fixed thirty-second beat through a three-minute wait
        // is nagging rather than reassurance.
        foreach (var (earlier, later) in delays.Zip(delays.Skip(1)))
        {
            Assert.True(later >= earlier, $"{later} came after {earlier} and was shorter.");
        }

        Assert.All(delays, delay => Assert.True(delay <= TimeSpan.FromMinutes(3)));
        Assert.Equal(TimeSpan.FromMinutes(3), delays[^1]);
    }
}
