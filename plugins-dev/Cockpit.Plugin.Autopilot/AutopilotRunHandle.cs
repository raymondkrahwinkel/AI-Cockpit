namespace Cockpit.Plugin.Autopilot;

// A running run and the task that completes when it settles — what `AutopilotRunManager` tracks so it can
// route a tool call to the right run and free the slot when it ends.
internal sealed record AutopilotRunHandle(AutopilotRunCoordinator Coordinator, Task Completed);
