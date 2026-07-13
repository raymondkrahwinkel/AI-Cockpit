namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// One transition a workflow allows from where an issue stands now (a state-machine field's
/// <c>possibleEvents</c>). <see cref="Presentation"/> is what the operator sees and what is written back to fire
/// it — the event's name, e.g. "start progress".
/// </summary>
internal sealed record YouTrackStateEvent(string Id, string Presentation);
