using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Gate;
using Cockpit.Plugin.LocalCi.Mcp;
using Cockpit.Plugin.LocalCi.Runtime;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugin.LocalCi.Ui;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.LocalCi;

// Local CI plugin entry point (AC-448). It works out whether this machine can run a workflow job at all, reads the
// project's workflows to say which jobs are worth trying, and runs one of them in a container on the session's own
// checkout — with the log while it happens and a way to stop it.
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

        host.AddSettings(() => new LocalCiSettingsControl(runtime, settings));

        // Docker Desktop may well have been started while the dialog was open; a save is the cheapest honest moment
        // to stop trusting a stale answer.
        host.OnSettingsSaved(runtime.Invalidate);

        // A container this plugin started on an agent's say-so belongs in the status bar with a Kill only the
        // operator can press (AC-82).
        host.AddSupervisedActivityProvider(tracker);

        // Built once per session panel, which is the only place the host hands over a session's own context — so
        // it is both where the last run is shown and where the pane-to-checkout answer the MCP tools need is
        // learned. There is no lookup for that: see SessionCheckouts.
        var checkouts = new SessionCheckouts();
        host.AddSessionHeaderItem(session =>
        {
            checkouts.Remember(session);
            return new LocalCiSessionBadge(session, tracker);
        });

        // The gate, as something the two places that open a pull request can ask about. Off in every checkout
        // until the operator switches it on, and it answers "did not run" rather than "passed" when there is
        // nothing to go on — see PullRequestGate.
        var gateSettings = new PullRequestGateSettings(host.Storage);
        var gate = new PullRequestGateIntent(host, new PullRequestGate(tracker, gateSettings, head));
        host.RegisterIntentHandler(PullRequestGateIntent.Action, gate.HandleAsync);

        // The agent's side: a session can check its own work before it pushes it. Every run goes through the
        // operator's consent, and the tools take no path — the checkout is the caller's own.
        _ = host.AddMcpEndpoint(
            "cockpit-local-ci",
            new LocalCiMcpTools(host, checkouts, runner, tracker, head),
            isEnabled: () => settings.McpEnabled);

        // From the session's own header, so the run is about the checkout that session is working in rather than
        // whichever pane happens to be selected when the operator gets to it.
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Run CI on this machine…",
            "",
            session => _ = _OpenForAsync(host, session, runner, tracker, head, gateSettings))
        {
            IconKind = MaterialIconKind.FlaskOutline,
        });
    }

    public void Dispose()
    {
        _runner?.Dispose();
        _runtime?.Dispose();
    }

    private static Task _OpenForAsync(
        ICockpitHost host,
        IPluginSessionContext session,
        ILocalJobRunner runner,
        LocalRunTracker tracker,
        GitHead head,
        PullRequestGateSettings gate)
    {
        if (session.WorkingDirectory is not { Length: > 0 } projectRoot)
        {
            host.ShowToast(
                "This session has not said which directory it is working in yet, so there is no checkout to run.",
                PluginToastSeverity.Warning);
            return Task.CompletedTask;
        }

        return host.ShowDialogAsync(
            "Local CI",
            () => new LocalCiRunView(projectRoot, runner, tracker, head.ReadAsync, gate),
            singleInstanceKey: $"run.{session.PaneId}",
            width: 900,
            height: 640);
    }
}
