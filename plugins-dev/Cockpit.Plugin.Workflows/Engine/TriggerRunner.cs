namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// A trigger that something else fired: the watcher saw the text, the clock came round. By the time the run exists
/// the trigger has already happened, so its step has nothing left to do but hand on what it was fired with — the
/// matched text, the session it came from, the time.
/// <para>
/// It exists rather than being skipped so that the run shows the trigger as its first step, with the data it carried.
/// A run whose first step is missing reads as a run that started from nowhere.
/// </para>
/// </summary>
internal sealed class TriggerRunner(string typeId) : IStepRunner
{
    public string TypeId => typeId;

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.FromResult(StepOutcome.Passing(context.Input, "Fired."));
}
