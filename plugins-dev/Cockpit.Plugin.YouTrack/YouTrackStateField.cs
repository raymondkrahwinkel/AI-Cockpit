namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The custom field that carries an issue's status, as the project actually defines it — read per issue rather
/// than assumed, because the field is called "State" on one board and "Stage" or "Kanban State" on another, and
/// because which values exist (does this board even have a Review step?) is a property of the project, not of
/// the cockpit.
/// <para>
/// YouTrack has two kinds. An ordinary field (<c>StateIssueCustomField</c>) is set by writing a value. A field
/// governed by a workflow (<c>StateMachineIssueCustomField</c>) is <em>not</em>: you fire one of its
/// <see cref="PossibleEvents"/>, and any transition the workflow does not define is refused. So the two are
/// kept apart here rather than pretending every board is a free-for-all.
/// </para>
/// </summary>
/// <param name="Id">The field's id, needed to read a state-machine field's possible events.</param>
/// <param name="Name">The field's name on this project ("State", "Stage", …) — how the update addresses it.</param>
/// <param name="Type">The field's <c>$type</c>, echoed back on update: a wrong one is a 500, not a validation error.</param>
/// <param name="CurrentValue">The issue's current status, or null when the field has no value yet.</param>
/// <param name="Values">The values this project allows, empty when the token may not read them (see <see cref="YouTrackClient"/>).</param>
/// <param name="PossibleEvents">For a state-machine field: the transitions allowed from where the issue is now. Empty otherwise.</param>
internal sealed record YouTrackStateField(
    string Id,
    string Name,
    string Type,
    string? CurrentValue,
    IReadOnlyList<string> Values,
    IReadOnlyList<YouTrackStateEvent> PossibleEvents)
{
    public const string StateMachineType = "StateMachineIssueCustomField";

    /// <summary>True when a workflow governs this field: transitions go through <see cref="PossibleEvents"/>, not by writing a value.</summary>
    public bool IsStateMachine => string.Equals(Type, StateMachineType, StringComparison.Ordinal);

    /// <summary>What the operator can move this issue to right now — the events for a state-machine field, the allowed values otherwise. Empty means: offer nothing, rather than offer something that will be refused.</summary>
    public IReadOnlyList<string> AvailableTargets =>
        IsStateMachine
            ? PossibleEvents.Select(possibleEvent => possibleEvent.Presentation).ToList()
            : Values.Where(value => !string.Equals(value, CurrentValue, StringComparison.Ordinal)).ToList();
}
