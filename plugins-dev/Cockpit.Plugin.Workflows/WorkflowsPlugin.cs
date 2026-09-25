using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Workflows.Contracts;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

// Workflows (#69): a canvas where flows are drawn — triggers, actions and decisions, wired together — and an engine
// that runs them, handing each step the data the one before it produced.
//
// What a flow can *do* is not fixed here. Any plugin may contribute a step
// (`ICockpitHost.AddWorkflowStep`): YouTrack knows how to move a ticket and this plugin never has to.
// The contributed steps are read when the editor asks for them rather than at startup, because plugins initialise in
// an order nobody controls and a step registered after us would otherwise be invisible until the next run of the app.
//
// AC-1399: the backend part — the engine, the watcher, the store, the run history and the MCP endpoint. The canvas,
// editor and settings view are the UI part (WorkflowsUi), which reads, saves and runs flows over the plugin's channel.
public sealed class WorkflowsPlugin : ICockpitPlugin
{
    private readonly List<IDisposable> _handlers = [];
    private FlowWatcher? _watcher;

    public PluginMetadata Metadata { get; } = new(
        Id: "workflows",
        DisplayName: "Workflows",
        Author: "Cockpit",
        Description: "Draw a flow and run it: a manual trigger, a shell command, a decision (If, or a Switch with a way out per case), a notification — wired together on a canvas and saved as you draw. A step uses what the steps before it produced ({output}, or {Run a command.output} to reach further back), and a decision's condition is an expression over that same data. Double-click a step to open it: what comes in on the left, its settings in the middle, what it produced on the right. Other plugins can contribute their own steps, so a flow can do whatever they know how to do.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var store = new WorkflowStore(host.Storage);
        RunStore.Migrate(host.Storage, host.Cache);
        var runs = new RunStore(host.Cache, run => host.Channel.Publish(WorkflowsChannel.RunRecorded, _Json(run)));
        var marks = new ScheduleMarks(host.Cache);
        var settings = new WorkflowsSettings(host.Storage);

        // The triggers that fire by themselves. Started here rather than when the editor opens: a flow that only runs
        // while you are looking at the editor is not automation, it is a button with extra steps. The grace is read
        // live off `settings` on every tick, same as the MCP toggle below, so changing it takes effect without a restart.
        _watcher = new FlowWatcher(store, runs, marks, host, () => TimeSpan.FromMinutes(settings.CatchUpGraceMinutes));

        // AC-12: the plugin's own MCP server, so agents can list, read, run and create/edit workflows. Contributed
        // through the host's endpoint mechanism (#AC-13) — it appears as the cockpit-workflows MCP. Fire-and-forget,
        // as the host asks. AC-40: gated on the plugin's own setting, read live each time a session's servers are
        // gathered, so the Workflows-settings toggle takes effect without a restart; the settings view edits it.
        _ = host.AddMcpEndpoint("cockpit-workflows", new WorkflowMcpTools(store, runs, host), isEnabled: () => settings.McpEnabled);

        _Handle(host, WorkflowsChannel.Load, (_, _) => Task.FromResult<object?>(WorkflowJson.WriteAll(store.Load())));
        _Handle(host, WorkflowsChannel.Save, (payload, _) =>
        {
            store.Save(WorkflowJson.ReadAll(_Read<WorkflowsSaveRequest>(payload).Json));
            return Task.FromResult<object?>(null);
        });
        _Handle(host, WorkflowsChannel.Catalog, (_, _) => Task.FromResult<object?>(_Catalog(host)));
        _Handle(host, WorkflowsChannel.Suggest, async (payload, cancellationToken) =>
        {
            var request = _Read<WorkflowsSuggestRequest>(payload);
            return host.WorkflowSteps.FirstOrDefault(step => step.TypeId == request.TypeId) is { } step
                ? await step.SuggestAsync(request.Parameter, cancellationToken)
                : Array.Empty<string>();
        });
        _Handle(host, WorkflowsChannel.Templates, (_, _) => Task.FromResult<object?>(host.WorkflowTemplates));
        _Handle(host, WorkflowsChannel.LastRun, (payload, _) =>
        {
            var workflowId = _Read<WorkflowsLastRunRequest>(payload).WorkflowId;
            return Task.FromResult<object?>(runs.Load().FirstOrDefault(run => run.WorkflowId == workflowId));
        });
        _Handle(host, WorkflowsChannel.Run, async (payload, _) =>
        {
            var request = _Read<WorkflowsRunRequest>(payload);
            var workflow = WorkflowJson.Read(request.WorkflowJson)
                ?? throw new ArgumentException("That is not a flow this build can read.", nameof(payload));

            // The same engine the watcher uses: a flow must not do different things depending on who started it.
            var run = await EngineFactory.Create(host, host.WorkflowSteps).RunAsync(workflow, request.TriggerNodeId, RunOrigin.Operator);
            runs.Add(run);
            return run;
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    // Read now, not at startup: a step registered after this plugin would otherwise not exist until the app restarts.
    // A non-trigger step that did not declare whether it needs consent (#AC-38) is left out rather than run ungated —
    // and named, so the plugin that shipped it can be fixed. The backend's own catalog follows, for the MCP tools.
    private static WorkflowsStepCatalog _Catalog(ICockpitHost host)
    {
        var contributed = host.WorkflowSteps;
        var usable = contributed.Where(step => !ContributedStep.IsUndeclared(step)).Select(ContributedStep.Describe).ToList();
        NodeCatalog.Contribute(usable);

        return new WorkflowsStepCatalog(
            [.. usable.Select(WorkflowsStepType.Of)],
            [.. contributed.Where(ContributedStep.IsUndeclared).Select(step => step.TypeId)]);
    }

    private void _Handle(ICockpitHost host, string action, Func<JsonElement, CancellationToken, Task<object?>> answer) =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) => _Json(await answer(payload, cancellationToken))));

    private static T _Read<T>(JsonElement payload) =>
        payload.Deserialize<T>(WorkflowsChannel.Json) ?? throw new ArgumentException($"The request carries no {typeof(T).Name}.", nameof(payload));

    private static JsonElement _Json(object? value) => JsonSerializer.SerializeToElement(value, WorkflowsChannel.Json);
}
