using Cockpit.Plugin.Workflows.Contracts;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows.UI;

// The UI part of Workflows (AC-1399): the settings view and the editor window, opened from the left menu or its
// shortcut. The backend part, WorkflowsPlugin, keeps the flows, runs them and knows the contributed steps; this part
// asks it over the plugin's channel.
public sealed class WorkflowsUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new WorkflowsSettings(host.Storage);
        host.AddSettings(() => new WorkflowsSettingsControl(host, settings));

        host.AddSideMenuButton("Workflows", () => _OpenEditor(host));
        host.AddShortcut(new PluginShortcut("workflows.open", "Workflow editor", "Ctrl+Shift+W", () => _OpenEditor(host)));
    }

    // A menu click cannot await, so the editor is awaited here and a failure to open it is told rather than lost.
    private static async void _OpenEditor(ICockpitUiHost host)
    {
        try
        {
            await OpenEditorAsync(host);
        }
        catch (Exception exception)
        {
            host.ShowToast($"Could not open Workflows: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    internal static async Task OpenEditorAsync(ICockpitUiHost host)
    {
        var catalog = await host.Channel.AskAsync<WorkflowsStepCatalog>(WorkflowsChannel.Catalog)
            ?? throw new InvalidOperationException("The backend sent no step catalog.");
        if (catalog.Undeclared.Count > 0)
        {
            host.ShowToast(
                $"Left out {catalog.Undeclared.Count} workflow step(s) that do not declare RequiredConsent: {string.Join(", ", catalog.Undeclared)}. Their plugin must set it — None for a safe step, Dangerous for one that acts with your rights.",
                PluginToastSeverity.Warning);
        }

        NodeCatalog.Contribute([.. catalog.Contributed.Select(type => type.Describe(async (parameter, cancellationToken) =>
            await host.Channel.AskAsync<List<string>>(WorkflowsChannel.Suggest, new WorkflowsSuggestRequest(type.Id, parameter), cancellationToken) ?? []))]);

        var workflows = WorkflowJson.ReadAll(await host.Channel.AskAsync<string>(WorkflowsChannel.Load));
        var templates = await host.Channel.AskAsync<List<WorkflowTemplate>>(WorkflowsChannel.Templates) ?? [];

        // Ask big: a canvas is the one thing that is never too large, and the host clamps the request to the cockpit
        // window anyway. One dialog per plugin: reopening while it's up should refocus it, not stack a second one.
        await host.ShowDialogAsync("Workflows", () => new WorkflowsDialogControl(host, [.. workflows], templates), "workflows", width: 1600, height: 1000);
    }
}
