using System.Text.Json;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Plugin.LocalCi.Contracts;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Gate;
using Cockpit.Plugin.LocalCi.Mcp;
using Cockpit.Plugin.LocalCi.Runtime;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugin.LocalCi.Workflows;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.LocalCi;

// Local CI plugin entry point (AC-448). It works out whether this machine can run a workflow job at all, reads the
// project's workflows to say which jobs are worth trying, and runs one of them in a container on the session's own
// checkout — with the log while it happens and a way to stop it.
// AC-1394: the backend part. Its UI part, LocalCiUi (the settings page, the run dialog, the session badge), lives
// in UI/, its own project, which builds into this project's bin/; everything it asks is answered here over the
// plugin's channel.
public sealed class LocalCiPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "local-ci",
        DisplayName: "Local CI",
        Author: "Cockpit",
        Description: "Run your GitHub workflow jobs on this machine's own Docker, from the session that is working " +
            "on the code. Docker is reported in three states — missing, installed but the engine is not answering, " +
            "or ready — and each job in the project's workflows is either offered or refused with the concrete " +
            "reason it cannot run here. A job runs whole or not at all: what this plugin does not understand it " +
            "refuses rather than skipping quietly. A session can start a run itself and read the verdict back, " +
            "with you approving the exact command each time, and a checkout can be set to hold back its pull " +
            "requests until a local run has passed. The result says it ran on this machine, never that CI is green.");

    private readonly List<IDisposable> _handlers = [];

    private LocalCiRuntime? _runtime;
    private LocalJobRunner? _runner;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new LocalCiSettings(host.Storage);

        // One runtime for the whole plugin: the detection is the answer everything downstream reads, so it is taken
        // once and cached rather than re-probed per caller.
        var cli = new CliRunner();
        var runtime = new LocalCiRuntime(cli);
        _runtime = runtime;

        var tracker = new LocalRunTracker();
        var head = new GitHead(cli);
        var runner = new LocalJobRunner(
            runtime,
            new StreamingCliRunner(),
            new DockerRunCleanup(cli),
            () => ActRunOptions.For(Environment.ProcessorCount, settings.RunnerImage),
            () => Guid.NewGuid().ToString("n"));
        _runner = runner;

        // Docker Desktop may well have been started while the settings page was open; a save is the cheapest honest
        // moment to stop trusting a stale answer.
        host.OnSettingsSaved(runtime.Invalidate);

        // A container this plugin started on an agent's say-so belongs in the status bar with a Kill only the
        // operator can press (AC-82).
        host.AddSupervisedActivityProvider(tracker);

        // The UI part's RunChanged tells every open badge and run dialog to re-ask LastRun.
        tracker.Changed += () => host.Channel.Publish(LocalCiChannel.RunChanged, _Empty());

        // Which checkout each session is working in, by pane — the MCP tools' lookup. The UI part is the only place
        // a session's own context arrives, so it is learned over the channel (RememberCheckout) rather than here.
        var checkouts = new SessionCheckouts();

        // A closed pane must drop out of the checkout map — without it a pane that never comes back still pins its
        // last-known working directory forever.
        host.Sessions.SessionClosed += (_, paneId) => checkouts.Forget(paneId);

        // The gate, as something the two places that open a pull request can ask about. Off in every checkout
        // until the operator switches it on, and it answers "did not run" rather than "passed" when there is
        // nothing to go on — see PullRequestGate.
        var gateSettings = new PullRequestGateSettings(host.Storage);
        var gate = new PullRequestGateIntent(host, new PullRequestGate(tracker, gateSettings, head));
        host.RegisterIntentHandler(PullRequestGateIntent.Action, gate.HandleAsync);

        // The agent's side: a session can check its own work before it pushes it. Every run goes through the
        // operator's consent, and the checkout is the caller's own — or one it made for itself via worktree_create
        // (AC-1015), never an arbitrary path.
        var worktrees = host.Services.GetService(typeof(IWorktreeManager)) as IWorktreeManager;
        _ = host.AddMcpEndpoint(
            "cockpit-local-ci",
            new LocalCiMcpTools(host, checkouts, runner, tracker, head, settings, worktrees),
            isEnabled: () => settings.McpEnabled);

        _handlers.Add(host.Channel.Handle(LocalCiChannel.RuntimeStatus, async (_, cancellationToken) =>
        {
            var status = await runtime.GetStatusAsync(cancellationToken);
            var dto = new LocalCiRuntimeStatusInfo(status.Docker.IsReady, status.Docker.Message, status.Act.IsInstalled, status.Act.Message);
            return JsonSerializer.SerializeToElement(dto, LocalCiChannel.Json);
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.InvalidateRuntime, (_, _) =>
        {
            runtime.Invalidate();
            return Task.FromResult(_Empty());
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.Jobs, (payload, _) =>
        {
            var request = _Read<LocalCiProjectRequest>(payload);
            var workflows = WorkflowCatalog.ReadProject(request.ProjectRoot).Select(_ToWorkflowJobs).ToList();
            return Task.FromResult(JsonSerializer.SerializeToElement<IReadOnlyList<LocalCiWorkflowJobs>>(workflows, LocalCiChannel.Json));
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.Run, async (payload, cancellationToken) =>
        {
            var request = _Read<LocalCiRunRequest>(payload);
            var result = await _RunTrackedAsync(host, runner, tracker, head, request, cancellationToken);
            return JsonSerializer.SerializeToElement(new LocalCiRunResult(result.Headline), LocalCiChannel.Json);
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.LastRun, (payload, _) =>
        {
            var request = _Read<LocalCiProjectRequest>(payload);
            var last = tracker.LastFor(request.ProjectRoot);
            var summary = last is null ? null : new LocalCiRunSummary(last.Result.JobId, _OutcomeTag(last.Result.Outcome), last.Result.Headline);
            return Task.FromResult(JsonSerializer.SerializeToElement(summary, LocalCiChannel.Json));
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.GateGet, (payload, _) =>
        {
            var request = _Read<LocalCiProjectRequest>(payload);
            return Task.FromResult(JsonSerializer.SerializeToElement(gateSettings.IsOnFor(request.ProjectRoot), LocalCiChannel.Json));
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.GateSet, (payload, _) =>
        {
            var request = _Read<LocalCiGateSetRequest>(payload);
            gateSettings.Set(request.ProjectRoot, request.On);
            return Task.FromResult(_Empty());
        }));

        _handlers.Add(host.Channel.Handle(LocalCiChannel.RememberCheckout, (payload, _) =>
        {
            var info = _Read<LocalCiCheckoutInfo>(payload);
            checkouts.Remember(new RemoteSessionCheckout(info.PaneId, info.WorkingDirectory));
            return Task.FromResult(_Empty());
        }));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();

        _runner?.Dispose();
        _runtime?.Dispose();
    }

    // The order a run started from the UI part follows: read the commit, register it with the tracker (so the
    // status bar's Kill and a concurrent LastRun both see it), run it, record the result — same shape as the MCP
    // tools' own RunLocalChecks, kept separate since Mcp/LocalCiMcpTools.cs stays untouched by this split.
    private static async Task<LocalRunResult> _RunTrackedAsync(
        ICockpitHost host,
        ILocalJobRunner runner,
        LocalRunTracker tracker,
        GitHead head,
        LocalCiRunRequest request,
        CancellationToken cancellationToken)
    {
        var runRequest = new LocalRunRequest(request.ProjectRoot, request.WorkflowPath, request.JobId);
        var startedAt = DateTimeOffset.UtcNow;
        var commit = await head.ReadAsync(request.ProjectRoot, cancellationToken);

        // Linked rather than passed through directly: the tracker's own stop callback (the status bar's Kill,
        // AC-82) must be able to cancel this run even when the UI part's own Stop button never fires.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tracker.Begin(request.ProjectRoot, request.JobId, startedAt, () =>
        {
            stopping.Cancel();
            return Task.CompletedTask;
        });

        var result = LocalRunResult.DidNotRun(
            request.WorkflowPath, request.JobId, LocalRunOutcome.Cancelled, "the run ended without a verdict.");
        try
        {
            result = await runner.RunAsync(
                runRequest,
                line => host.Channel.Publish(
                    LocalCiChannel.RunLine,
                    JsonSerializer.SerializeToElement(
                        new LocalCiRunLine(request.ProjectRoot, request.JobId, line), LocalCiChannel.Json)),
                approve: null,
                stopping.Token);
        }
        finally
        {
            tracker.Complete(request.ProjectRoot, result, commit, DateTimeOffset.UtcNow);
        }

        return result;
    }

    private static LocalCiWorkflowJobs _ToWorkflowJobs(WorkflowParseResult read) =>
        read.Document is { } document
            ? new LocalCiWorkflowJobs(read.Path, null, [.. LocalRunClassifier.Classify(document).Select(_ToJobVerdict)])
            : new LocalCiWorkflowJobs(read.Path, read.Error, []);

    private static LocalCiJobVerdict _ToJobVerdict(JobVerdict verdict) =>
        new(verdict.JobId, verdict.DisplayName, verdict.CanRunLocally, verdict.Reason);

    private static string _OutcomeTag(LocalRunOutcome outcome) => outcome switch
    {
        LocalRunOutcome.Passed => "Passed",
        LocalRunOutcome.Failed => "Failed",
        _ => "Other",
    };

    private static T _Read<T>(JsonElement payload) =>
        payload.Deserialize<T>(LocalCiChannel.Json) ?? throw new ArgumentException("The request carries no data.", nameof(payload));

    private static JsonElement _Empty() => JsonSerializer.SerializeToElement<object?>(null, LocalCiChannel.Json);

    // Lets the UI part's only view of a session (IPluginSessionContext, handed to it when it builds a session
    // header item) reach SessionCheckouts.Remember, which wants that same type — even though the UI part sends
    // only a pane id and a directory over the channel.
    private sealed class RemoteSessionCheckout(string paneId, string? workingDirectory) : IPluginSessionContext
    {
        public string PaneId => paneId;

        public string? WorkingDirectory => workingDirectory;

        public event EventHandler? WorkingDirectoryChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }
    }
}
