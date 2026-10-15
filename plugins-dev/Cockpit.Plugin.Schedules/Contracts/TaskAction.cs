namespace Cockpit.Plugin.Schedules.Contracts;

// What a task does when it fires. A notification delivers `Text` as it is; the agent prompt comes with A2 (AC-1543).
public sealed record TaskAction(TaskActionKind Kind, string Text);
