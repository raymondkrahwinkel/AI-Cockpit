namespace Cockpit.Plugin.YouTrack;

// The custom field that carries an issue's status, as the project actually defines it — read per issue rather
// than assumed, because the field is called "State" on one board and "Stage" or "Kanban State" on another, and
// because which values exist (does this board even have a Review step?) is a property of the project, not of
// the cockpit.
//
// YouTrack has two kinds. An ordinary field (`StateIssueCustomField`) is set by writing a value. A field
// governed by a workflow (`StateMachineIssueCustomField`) is *not*: you fire one of its
// `PossibleEvents`, and any transition the workflow does not define is refused. So the two are
// kept apart here rather than pretending every board is a free-for-all.
//
// `Id`: The field's id, needed to read a state-machine field's possible events.
// `Name`: The field's name on this project ("State", "Stage", …) — how the update addresses it.
// `Type`: The field's `$type`, echoed back on update: a wrong one is a 500, not a validation error.
// `CurrentValue`: The issue's current status, or null when the field has no value yet.
// `Values`: The values this project allows, empty when the token may not read them (see `YouTrackClient`).
// `PossibleEvents`: For a state-machine field: the transitions allowed from where the issue is now. Empty otherwise.
internal sealed record YouTrackStateField(
    string Id,
    string Name,
    string Type,
    string? CurrentValue,
    IReadOnlyList<string> Values,
    IReadOnlyList<YouTrackStateEvent> PossibleEvents)
{
    public const string StateMachineType = "StateMachineIssueCustomField";

    // True when a workflow governs this field: transitions go through `PossibleEvents`, not by writing a value.
    public bool IsStateMachine => string.Equals(Type, StateMachineType, StringComparison.Ordinal);

    // What the operator can move this issue to right now — the events for a state-machine field, the allowed values otherwise. Empty means: offer nothing, rather than offer something that will be refused.
    public IReadOnlyList<string> AvailableTargets =>
        IsStateMachine
            ? PossibleEvents.Select(possibleEvent => possibleEvent.Presentation).ToList()
            : Values.Where(value => !string.Equals(value, CurrentValue, StringComparison.Ordinal)).ToList();
}
