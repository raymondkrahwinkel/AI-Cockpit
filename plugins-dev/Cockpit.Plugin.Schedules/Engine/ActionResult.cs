using Cockpit.Plugin.Schedules.Contracts;

namespace Cockpit.Plugin.Schedules.Engine;

// What an action reports back to the engine, which writes it into the run.
internal sealed record ActionResult(RunOutcome Outcome, string? Output = null, string? Reason = null)
{
    public string? DelegatedTaskId { get; init; }
}
