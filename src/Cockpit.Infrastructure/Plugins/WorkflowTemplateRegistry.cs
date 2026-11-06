using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The ready-made flows plugins contribute (#69), held by the host for the same reason its steps are: the workflows
/// plugin must not know that YouTrack exists, and YouTrack must not reference the workflows plugin to offer it a flow.
/// Both know the host, and the host knows nothing about either.
/// </summary>
public interface IWorkflowTemplateRegistry
{
    IReadOnlyList<WorkflowTemplate> Templates { get; }

    void Register(WorkflowTemplate template);

    /// <summary>
    /// Withdraws <paramref name="template"/>; false when it is not registered.
    /// </summary>
    bool Unregister(WorkflowTemplate template);

    event EventHandler? Changed;
}

internal sealed class WorkflowTemplateRegistry : IWorkflowTemplateRegistry, ISingletonService
{
    private readonly List<WorkflowTemplate> _templates = [];

    public event EventHandler? Changed;

    public IReadOnlyList<WorkflowTemplate> Templates
    {
        get
        {
            lock (_templates)
            {
                return [.. _templates];
            }
        }
    }

    // Same id twice is a picker with two entries the operator cannot tell apart, and an update that silently replaces
    // a flow they thought they knew.
    public void Register(WorkflowTemplate template)
    {
        lock (_templates)
        {
            if (_templates.Any(existing => string.Equals(existing.Id, template.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A workflow template called '{template.Id}' is already registered. Ids must be unique — prefix yours with your plugin's name.");
            }

            _templates.Add(template);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(WorkflowTemplate template)
    {
        lock (_templates)
        {
            if (!_templates.Remove(template))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
