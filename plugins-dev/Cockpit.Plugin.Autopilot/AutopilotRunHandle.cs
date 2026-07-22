namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A running run and the task that completes when it settles — what <see cref="AutopilotRunManager"/> tracks so it can
/// route a tool call to the right run and free the slot when it ends.
/// </summary>
internal sealed record AutopilotRunHandle(AutopilotRunCoordinator Coordinator, Task Completed);
