using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// When a finished session falls quiet: only a session that is actually done drops back to idle, and only once
/// it has been quiet for the whole threshold. A session that is busy or waiting on you is never idle, however
/// long it sits there — the waiting is the work.
/// </summary>
public class SessionIdleDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    // Only a session that is actually done drops back to idle, and only once it has been quiet for the whole
    // threshold. A session that is busy or waiting on you is never idle however long it sits there — the waiting
    // is the work — and a zero threshold turns the rule off entirely.
    [Theory]
    [InlineData(true, 6, 5, true)]
    [InlineData(true, 4, 5, false)]
    [InlineData(false, 180, 5, false)]
    [InlineData(true, 180, 0, false)]
    public void BecomesIdle_OnlyForAFinishedSessionThatHasBeenQuietLongEnough(
        bool isFinished, int quietMinutes, int thresholdMinutes, bool expected)
    {
        Assert.Equal(
            expected,
            SessionIdleDecision.BecomesIdle(
                isFinished, Now.AddMinutes(-quietMinutes), Now, TimeSpan.FromMinutes(thresholdMinutes)));
    }
}
