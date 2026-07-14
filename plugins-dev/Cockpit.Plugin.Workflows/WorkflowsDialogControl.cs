using Avalonia.Controls;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The workflow window (#69): it shows the manager — the flows you have — and swaps to the editor when you open
/// one. Two views rather than one, because keeping flows and building a flow are different jobs: the manager is
/// where you arm, duplicate and throw away; the editor is where a flow is drawn, and it wants the whole window.
/// </summary>
internal sealed class WorkflowsDialogControl : UserControl
{
    private readonly WorkflowStore _store;
    private readonly ICockpitHost _host;
    private readonly RunStore _runs;
    private readonly IReadOnlyList<IWorkflowStep> _contributed;
    private readonly List<Workflow> _workflows;
    private readonly WorkflowManagerControl _manager;

    public WorkflowsDialogControl(WorkflowStore store, ICockpitHost host, RunStore runs, IReadOnlyList<IWorkflowStep> contributed)
    {
        _store = store;
        _host = host;
        _runs = runs;
        _contributed = contributed;
        _workflows = [.. store.Load()];

        _manager = new WorkflowManagerControl(_workflows, host, host.WorkflowTemplates, _Save);
        _manager.OpenRequested += (_, workflow) => _Open(workflow);

        Content = _manager;
    }

    private void _Open(Workflow workflow)
    {
        var editor = new WorkflowEditorControl(workflow, _Save, _host, _runs, _contributed);
        editor.BackRequested += (_, _) =>
        {
            _manager.Refresh();
            Content = _manager;
        };

        Content = editor;
    }

    private void _Save() => _store.Save(_workflows);
}
