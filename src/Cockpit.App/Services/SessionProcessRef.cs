namespace Cockpit.App.Services;

// AC-1096: one session to weigh. Identity and label are two things here: a pane id is unique, a title is what the
// operator reads and two sessions are allowed to carry the same one. Keying the measurement on the title merged
// those two silently — one of the pair went unmeasured and the other could be shown its neighbour's figure.
public sealed record SessionProcessRef(string PaneId, string Title, int ProcessId);
