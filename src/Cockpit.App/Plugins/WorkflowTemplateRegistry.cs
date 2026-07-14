using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.App.Plugins;

/// <summary>
/// The ready-made flows plugins contribute (#69), held by the host for the same reason its steps are: the workflows
/// plugin must not know that YouTrack exists, and YouTrack must not reference the workflows plugin to offer it a flow.
/// Both know the host, and the host knows nothing about either.
/// </summary>
public interface IWorkflowTemplateRegistry
{
    IReadOnlyList<WorkflowTemplate> Templates { get; }

    void Register(WorkflowTemplate template);
}

internal sealed class WorkflowTemplateRegistry : IWorkflowTemplateRegistry, ISingletonService
{
    private readonly List<WorkflowTemplate> _templates = [];

    public IReadOnlyList<WorkflowTemplate> Templates => _templates;

    // Same id twice is a picker with two entries the operator cannot tell apart, and an update that silently replaces
    // a flow they thought they knew.
    public void Register(WorkflowTemplate template)
    {
        if (_templates.Any(existing => string.Equals(existing.Id, template.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A workflow template called '{template.Id}' is already registered. Ids must be unique — prefix yours with your plugin's name.");
        }

        _templates.Add(template);
    }
}
