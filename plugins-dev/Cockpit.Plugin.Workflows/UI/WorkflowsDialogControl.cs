using Avalonia.Controls;
using Cockpit.Plugin.Workflows.Contracts;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows.UI;

// The workflow window (#69): it shows the manager — the flows you have — and swaps to the editor when you open
// one. Two views rather than one, because keeping flows and building a flow are different jobs: the manager is
// where you arm, duplicate and throw away; the editor is where a flow is drawn, and it wants the whole window.
internal sealed class WorkflowsDialogControl : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly List<Workflow> _workflows;
    private readonly WorkflowManagerControl _manager;
    private Task _saving = Task.CompletedTask;

    public WorkflowsDialogControl(ICockpitUiHost host, List<Workflow> workflows, IReadOnlyList<WorkflowTemplate> templates)
    {
        _host = host;
        _workflows = workflows;

        _manager = new WorkflowManagerControl(_workflows, host, templates, _Save);
        _manager.OpenRequested += async (_, workflow) => await _OpenAsync(workflow);

        Content = _manager;
    }

    // What a step has to work with comes from the last run, and a run outlives the session it was made in: the
    // history is asked for before the editor opens, so a flow run yesterday does not open saying nothing flowed.
    private async Task _OpenAsync(Workflow workflow)
    {
        try
        {
            var lastRun = await _host.Channel.AskAsync<WorkflowRun>(WorkflowsChannel.LastRun, new WorkflowsLastRunRequest(workflow.Id));
            var editor = new WorkflowEditorControl(workflow, _Save, _host, lastRun);
            editor.BackRequested += (_, _) =>
            {
                _manager.Refresh();
                Content = _manager;
            };

            Content = editor;
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not open '{workflow.Name}': {exception.Message}", PluginToastSeverity.Error);
        }
    }

    // Saved as you draw, to the backend part over the channel (AC-1399), from handlers that cannot await; a failure is
    // told rather than lost. Each save waits for the one before it, so an older snapshot never lands after a newer one.
    private async void _Save()
    {
        var saving = _SaveAfterAsync(_saving, WorkflowJson.WriteAll(_workflows));
        _saving = saving;
        try
        {
            await saving;
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not save your flows: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _SaveAfterAsync(Task previous, string json)
    {
        // The previous save's failure was already told by its own caller; this one only needs it to be over.
        await previous.ContinueWith(_ => { }, TaskScheduler.Default);
        await _host.Channel.TellAsync(WorkflowsChannel.Save, new WorkflowsSaveRequest(json));
    }
}
