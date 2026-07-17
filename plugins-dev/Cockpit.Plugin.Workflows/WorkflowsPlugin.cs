using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// Workflows (#69): a canvas where flows are drawn — triggers, actions and decisions, wired together — and an engine
/// that runs them, handing each step the data the one before it produced.
/// <para>
/// What a flow can <em>do</em> is not fixed here. Any plugin may contribute a step
/// (<see cref="ICockpitHost.AddWorkflowStep"/>): YouTrack knows how to move a ticket and this plugin never has to.
/// The contributed steps are read when the editor opens rather than at startup, because plugins initialise in an
/// order nobody controls and a step registered after us would otherwise be invisible until the next run of the app.
/// </para>
/// The canvas is written on plain Avalonia: every node-editor library depends on Avalonia.Xaml.Behaviors, which
/// has no Avalonia 12 release — see the spike under <c>spikes/spike-node-editor</c>.
/// </summary>
public sealed class WorkflowsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "workflows",
        DisplayName: "Workflows",
        Version: "0.23.0",
        Author: "Cockpit",
        Description: "Draw a flow and run it: a manual trigger, a shell command, a decision (If, or a Switch with a way out per case), a notification — wired together on a canvas and saved as you draw. A step uses what the steps before it produced ({output}, or {Run a command.output} to reach further back), and a decision's condition is an expression over that same data. Double-click a step to open it: what comes in on the left, its settings in the middle, what it produced on the right. Other plugins can contribute their own steps, so a flow can do whatever they know how to do.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    private Engine.FlowWatcher? _watcher;

    public void Initialize(ICockpitHost host)
    {
        var store = new WorkflowStore(host.Storage);
        var runs = new Engine.RunStore(host.Storage);

        // The triggers that fire by themselves. Started here rather than when the editor opens: a flow that only runs
        // while you are looking at the editor is not automation, it is a button with extra steps.
        _watcher = new Engine.FlowWatcher(store, runs, host);

        // AC-12: the plugin's own MCP server, so agents can list, read, run and create/edit workflows. Contributed
        // through the host's endpoint mechanism (#AC-13) — it appears as the cockpit-workflows MCP, tickable per
        // session. Fire-and-forget, as the host asks.
        _ = host.AddMcpEndpoint("cockpit-workflows", new WorkflowMcpTools(store, runs, host));

        // Ask big: a canvas is the one thing that is never too large, and the host clamps the request to the
        // cockpit window anyway.
        void OpenEditor()
        {
            // Read now, not at startup: plugins initialise in an order nobody controls, and a step registered after
            // this plugin would otherwise not exist until the app is restarted.
            var contributed = host.WorkflowSteps;

            // A non-trigger step that did not declare whether it needs consent (#AC-38) is left out rather than run
            // ungated — and named, so the plugin that shipped it can be fixed.
            var undeclared = contributed.Where(Engine.ContributedStep.IsUndeclared).ToList();
            if (undeclared.Count > 0)
            {
                host.ShowToast(
                    $"Left out {undeclared.Count} workflow step(s) that do not declare RequiredConsent: {string.Join(", ", undeclared.Select(step => step.TypeId))}. Their plugin must set it — None for a safe step, Dangerous for one that acts with your rights.",
                    PluginToastSeverity.Warning);
            }

            var usable = contributed.Where(step => !Engine.ContributedStep.IsUndeclared(step)).ToList();
            Model.NodeCatalog.Contribute([.. usable.Select(Engine.ContributedStep.Describe)]);

            _ = host.ShowDialogAsync("Workflows", () => new WorkflowsDialogControl(store, host, runs, usable), 1600, 1000);
        }

        host.AddSideMenuButton("Workflows", OpenEditor);
        host.AddShortcut(new PluginShortcut("workflows.open", "Workflow editor", "Ctrl+Shift+W", OpenEditor));
    }

    public void Dispose() => _watcher?.Dispose();
}
