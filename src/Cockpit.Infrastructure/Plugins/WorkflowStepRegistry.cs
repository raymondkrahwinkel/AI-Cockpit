using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The steps plugins contribute to the workflow editor (#69). The host holds them because the two plugins involved
/// cannot see each other: the workflows plugin must not know that YouTrack exists, and YouTrack must not reference
/// the workflows plugin to offer a step to it. Both know the host, and the host knows nothing about either.
/// </summary>
public interface IWorkflowStepRegistry
{
    IReadOnlyList<IWorkflowStep> Steps { get; }

    void Register(IWorkflowStep step);

    /// <summary>
    /// Withdraws <paramref name="step"/>; false when it is not registered.
    /// </summary>
    bool Unregister(IWorkflowStep step);

    event EventHandler? Changed;

    /// <summary>
    /// A plugin fired one of its triggers. The workflows plugin listens; nobody else has a reason to.
    /// </summary>
    event EventHandler<WorkflowTriggerFired>? Fired;

    void Raise(string typeId, IReadOnlyDictionary<string, string> data);
}

internal sealed class WorkflowStepRegistry(ILogger<WorkflowStepRegistry> logger) : IWorkflowStepRegistry, ISingletonService
{
    private readonly List<IWorkflowStep> _steps = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<IWorkflowStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public event EventHandler? Changed;

    // Two plugins claiming the same type id would make a flow's stored step ambiguous — which of them ran is then a
    // question of load order, and a flow that does different things on different days is worse than one that refuses.
    public void Register(IWorkflowStep step)
    {
        lock (_gate)
        {
            if (_steps.Any(existing => string.Equals(existing.TypeId, step.TypeId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A workflow step called '{step.TypeId}' is already registered. Type ids must be unique — prefix yours with your plugin's name.");
            }

            _steps.Add(step);
        }

        _RaiseChanged(step);
    }

    public bool Unregister(IWorkflowStep step)
    {
        lock (_gate)
        {
            if (!_steps.Remove(step))
            {
                return false;
            }
        }

        _RaiseChanged(step);
        return true;
    }

    private void _RaiseChanged(IWorkflowStep step)
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A workflow step change subscriber failed after '{TypeId}' changed.", step.TypeId);
            }
        }
    }

    public event EventHandler<WorkflowTriggerFired>? Fired;

    // Fired for a type nobody contributed as a trigger is a bug in the plugin that fired it, and one that would
    // otherwise show up as "my flow never runs". It says so instead.
    public void Raise(string typeId, IReadOnlyDictionary<string, string> data)
    {
        if (!Steps.Any(step => step.IsTrigger && string.Equals(step.TypeId, typeId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"'{typeId}' is not a registered workflow trigger, so nothing would ever start on it.");
        }

        Fired?.Invoke(this, new WorkflowTriggerFired(typeId, data));
    }
}
