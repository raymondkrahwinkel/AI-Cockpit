namespace Cockpit.Core.Sessions;

// The row one event landed on, as a snapshot, and whether it landed in a sub-agent's lane rather than the top level.
// AC-1437: the host also says whether the event was the top-level reply's own text, and whether it stopped the turn
// on the operator (a question, or a permission prompt left waiting), so a consumer need not read the event for it.
public readonly record struct TranscriptFold(TranscriptSnapshotEntry? Row, bool InSubAgentLane)
{
    public bool IsReply { get; init; }

    public bool AsksTheOperator { get; init; }
}
