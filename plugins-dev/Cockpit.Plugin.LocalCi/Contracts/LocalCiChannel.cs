using System.Text.Json;

namespace Cockpit.Plugin.LocalCi.Contracts;

// AC-1394: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class LocalCiChannel
{
    // act's own medium image (Execution/ActCommand.DefaultRunnerImage). Duplicated here as a literal rather than
    // referenced: Execution/ stays backend-only and out of the UI part's reach, and the value changes about as
    // often as never.
    public const string DefaultRunnerImage = "catthehacker/ubuntu:act-latest";

    // Payload: ignored. Answers a LocalCiRuntimeStatusInfo.
    public const string RuntimeStatus = "runtime-status";

    // Payload: ignored. Drops the runtime's cached probe so the next RuntimeStatus probes again. Answers nothing.
    public const string InvalidateRuntime = "invalidate-runtime";

    // Payload: LocalCiProjectRequest. Answers every workflow in the project, each with its jobs' verdicts.
    public const string Jobs = "jobs";

    // Payload: LocalCiRunRequest. Runs the job, publishing RunLine as it produces output, and answers its result
    // once it ends — passed, failed, or one of the reasons it did not reach a verdict.
    public const string Run = "run";

    // Published while a run is in progress: one line of its output. Payload: LocalCiRunLine.
    public const string RunLine = "run-line";

    // Payload: LocalCiProjectRequest. Answers the last finished run in that checkout, or null.
    public const string LastRun = "last-run";

    // Published whenever a run starts or finishes, in any checkout. Payload: ignored — a subscriber re-asks LastRun.
    public const string RunChanged = "run-changed";

    // Payload: LocalCiProjectRequest. Answers whether the pull-request gate is switched on for that checkout.
    public const string GateGet = "gate-get";

    // Payload: LocalCiGateSetRequest. Switches the pull-request gate on or off for that checkout. Answers nothing.
    public const string GateSet = "gate-set";

    // Payload: LocalCiCheckoutInfo. Records which checkout a pane is working in, for the MCP tools' pane-to-checkout
    // lookup (SessionCheckouts) — the UI part is the only place a session's own context arrives. Answers nothing.
    public const string RememberCheckout = "remember-checkout";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record LocalCiProjectRequest(string ProjectRoot);

internal sealed record LocalCiRunRequest(string ProjectRoot, string WorkflowPath, string JobId);

internal sealed record LocalCiGateSetRequest(string ProjectRoot, bool On);

internal sealed record LocalCiCheckoutInfo(string PaneId, string? WorkingDirectory);

// What the settings page shows about this machine. Named -Info, not -Status: the backend part already has its own
// internal LocalCiRuntimeStatus (Runtime/ILocalCiRuntime.cs), and the two are never interchangeable.
internal sealed record LocalCiRuntimeStatusInfo(bool DockerIsReady, string DockerMessage, bool ActIsInstalled, string ActMessage);

// One workflow file's jobs, or the reason it could not be read.
internal sealed record LocalCiWorkflowJobs(string WorkflowPath, string? Error, IReadOnlyList<LocalCiJobVerdict> Jobs);

internal sealed record LocalCiJobVerdict(string JobId, string DisplayName, bool CanRunLocally, string? Reason);

// What a finished run answers the caller that started it.
internal sealed record LocalCiRunResult(string Headline);

internal sealed record LocalCiRunLine(string ProjectRoot, string JobId, string Text);

// Outcome is "Passed", "Failed" or "Other" — the badge only ever shows a tick, a cross or a dash.
internal sealed record LocalCiRunSummary(string JobId, string Outcome, string Headline);
