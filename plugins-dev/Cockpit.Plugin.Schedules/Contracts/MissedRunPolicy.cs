namespace Cockpit.Plugin.Schedules.Contracts;

// What a task does with slots that passed while the machine slept or the cockpit was down (plan §4).
public enum MissedRunPolicy
{
    // At most one catch-up run for the newest missed slot, if it is not older than the task's period.
    RunOnceWhenBack,

    // Every missed slot is logged as missed, without a run.
    Skip,
}
