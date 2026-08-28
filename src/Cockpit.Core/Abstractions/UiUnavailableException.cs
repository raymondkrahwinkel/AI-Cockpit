namespace Cockpit.Core.Abstractions;

// AC-1138: a call that had to run on the cockpit's UI thread did not get there within its deadline. Its own type
// rather than a generic failure on purpose: this is what lets an agent tell "slow, try again later" from "broken",
// and the work it would have done is abandoned when this is thrown, so the effect never lands afterwards either.
public sealed class UiUnavailableException(TimeSpan deadline) : Exception(_Message(deadline))
{
    // What an agent branches on; the MCP endpoint host carries it out to the tool result as `code`.
    public const string Code = "ui_unavailable";

    public TimeSpan Deadline { get; } = deadline;

    private static string _Message(TimeSpan deadline) =>
        $"{Code}: the cockpit's UI thread did not answer within {deadline.TotalSeconds:0.##}s. It is blocked or "
        + "starved by higher-priority work; nothing was applied. Try again later, or another session's tools.";
}
