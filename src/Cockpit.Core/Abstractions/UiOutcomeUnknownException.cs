namespace Cockpit.Core.Abstractions;

// UI work began before its deadline, so its eventual effect is unknown and must not be retried.
public sealed class UiOutcomeUnknownException(TimeSpan deadline) : Exception(_Message(deadline))
{
    public const string Code = "ui_outcome_unknown";

    public TimeSpan Deadline { get; } = deadline;

    private static string _Message(TimeSpan deadline) =>
        $"{Code}: the cockpit's UI work began before its {deadline.TotalSeconds:0.##}s deadline. Its effect may still land; do not retry.";
}
