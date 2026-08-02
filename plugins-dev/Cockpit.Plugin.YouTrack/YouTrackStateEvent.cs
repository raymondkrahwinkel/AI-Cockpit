namespace Cockpit.Plugin.YouTrack;

// One transition a workflow allows from where an issue stands now (a state-machine field's
// `possibleEvents`). `Presentation` is what the operator sees and what is written back to fire
// it — the event's name, e.g. "start progress".
internal sealed record YouTrackStateEvent(string Id, string Presentation);
