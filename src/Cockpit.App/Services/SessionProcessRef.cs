namespace Cockpit.App.Services;

// AC-1096: one session to weigh, keyed on the pane id — two sessions may carry the same title, and keying on that
// merged them silently, leaving one unmeasured and showing the other its neighbour's figure. AC-1331: `RootIsShell`
// is true for a terminal pane, whose root shell itself runs the work.
public sealed record SessionProcessRef(string PaneId, string Title, int ProcessId, bool RootIsShell = false);
