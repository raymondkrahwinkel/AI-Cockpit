using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The workflow editor (#69): a left-menu button opens a canvas where flows are drawn — triggers, actions and
/// decisions, wired together — and saved to the plugin's own storage.
/// <para>
/// This is the editor, not the engine: nothing executes a flow yet. It ships anyway because the shape of a
/// workflow is the thing to get right first, and because the canvas is what tells us whether the model is any
/// good to work with. The dialog says as much on screen rather than letting a drawn flow look live.
/// </para>
/// The canvas is written on plain Avalonia: every node-editor library depends on Avalonia.Xaml.Behaviors, which
/// has no Avalonia 12 release — see the spike under <c>spikes/spike-node-editor</c>.
/// </summary>
public sealed class WorkflowsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "workflows",
        DisplayName: "Workflows",
        Version: "0.7.1",
        Author: "Cockpit",
        Description: "A visual editor for cockpit workflows: drop triggers, actions and decisions on a canvas, wire them together, and the flow is saved as you draw it. Drag a node by its header, pull a wire out of an output pin, Delete removes the selected node; the wheel zooms and dragging the background pans. The rules are the model's, not the canvas's — a trigger takes nothing in, a step continues one way at a time, and a flow cannot loop back on itself, each refusal saying why. Editor only for now: nothing executes these flows yet.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var store = new WorkflowStore(host.Storage);
        var runs = new Engine.RunStore(host.Storage);

        // Ask big: a canvas is the one thing that is never too large, and the host clamps the request to the
        // cockpit window anyway.
        void OpenEditor() =>
            _ = host.ShowDialogAsync("Workflows", () => new WorkflowsDialogControl(store, host, runs), 1600, 1000);

        host.AddSideMenuButton("Workflows", OpenEditor);
        host.AddShortcut(new PluginShortcut("workflows.open", "Workflow editor", "Ctrl+Shift+W", OpenEditor));
    }

    public void Dispose()
    {
    }
}
