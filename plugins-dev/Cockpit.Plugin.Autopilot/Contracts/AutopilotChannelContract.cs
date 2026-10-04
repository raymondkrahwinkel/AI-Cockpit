using System.Text.Json;

namespace Cockpit.Plugin.Autopilot;

// AC-1418: the wire protocol between AutopilotChannel (backend) and AutopilotChannelClient (UI): names, the JSON
// options and the messages. The workspace follows one state event per workspace, a whole snapshot each time, so the
// UI keeps the newest by seq; everything it does goes back as an action.
internal static class AutopilotChannelContract
{
    public const string PlanWorkspaceId = "workspace.autopilot.plan";

    // Events, backend to UI part.
    public const string OpenPlan = "open-plan";
    public const string OpenSettings = "open-settings";
    public const string State = "state";
    public const string TemplatesChanged = "templates-changed";

    // Action, payload the selected session's working directory or an empty string: UI part to backend part.
    public const string ActiveDirectory = "active-directory";

    // WorkspaceRef: a workspace opened (its runs start, a State follows) or was really closed (its runs stop).
    public const string Attach = "attach";
    public const string Detach = "detach";

    // No payload -> bool: opens a fresh planning round, false when one is already open.
    public const string BeginPlanning = "plan.begin";
    public const string CancelPlanning = "plan.cancel";

    // PlanningCeoRequest -> pane id or null: embeds the planning round's CEO in the workspace; ClosePlanningCeo ends it.
    public const string EmbedPlanningCeo = "plan.embed-ceo";
    public const string ClosePlanningCeo = "plan.close-ceo";

    // SubmitRequest -> bool: approves the round's draft as a run; false when the draft is no longer the one Shown.
    public const string Submit = "plan.submit";

    // RunRequest / MergeGoRequest / AnswerRequest: what the operator does to one active run.
    public const string Stop = "run.stop";
    public const string Intervene = "run.intervene";
    public const string Answer = "run.answer";
    public const string MergeGo = "run.merge-go";

    // MergeGoRequest with the epic as Issue: the epic's end gate.
    public const string EpicGo = "epic.go";

    // QueueRequest: reorder or drop a queued run; refused when the entry at Index is no longer Expected.
    public const string QueueMoveUp = "queue.up";
    public const string QueueMoveDown = "queue.down";
    public const string QueueRemove = "queue.remove";

    // No payload / CorrectionRequest: clear the history, or reclassify one settled step.
    public const string HistoryClear = "history.clear";
    public const string HistoryCorrect = "history.correct";

    // No payload -> AutopilotTemplateCatalog; the template edits the settings make.
    public const string Templates = "templates";
    public const string TemplateUpsertUser = "templates.upsert-user";
    public const string TemplateUpsertOverride = "templates.upsert-override";
    public const string TemplateDelete = "templates.delete";
    public const string TemplateReset = "templates.reset";

    // No payload -> string[]: the ids of the loaded trackers, which the settings offer an executable stage for.
    public const string Trackers = "trackers";

    // No payload -> PluginRememberedWorkingPaths: the folders the working-directory field offers.
    public const string RememberedPaths = "remembered-paths";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    // Whether two values say the same on the wire: how the backend tells the item the operator saw from what is there now.
    public static bool SameOnTheWire<T>(T left, T right) => ToJson(left).GetRawText() == ToJson(right).GetRawText();

    public static T Read<T>(JsonElement payload) =>
        payload.Deserialize<T>(Json) ?? throw new JsonException($"Channel payload is null where a {typeof(T).Name} is required.");
}

internal sealed record WorkspaceRef(string WorkspaceId);

internal sealed record PlanningCeoRequest(string WorkspaceId, string? ActiveDirectory, string? KickoffMessage);

internal sealed record SubmitRequest(AutopilotPlan Shown, string Name, string WorkingDirectory, bool DeliversPullRequest, AutopilotMergeMode MergeMode);

internal sealed record RunRequest(string WorkspaceId, string RunId);

internal sealed record AnswerRequest(string WorkspaceId, string RunId, string Text);

internal sealed record MergeGoRequest(string WorkspaceId, string RunId, string Issue, bool Go, string? Reason);

internal sealed record QueueRequest(int Index, AutopilotPlan Expected);

internal sealed record CorrectionRequest(string RunId, string FinishedAt, int StepIndex, AutopilotCorrectionKind Kind);

internal sealed record AutopilotTemplateCatalog(IReadOnlyList<AutopilotTemplate> Templates, IReadOnlyDictionary<string, string> PluginNames);

// One workspace's whole state: the planning round, its runs, the queue, the history and the epic at its end gate.
internal sealed record AutopilotWorkspaceState(
    string WorkspaceId,
    AutopilotPlanPhase PlanningPhase,
    AutopilotPlan? PlanningPlan,
    string? PlanningCeoPaneId,
    bool PlanningCeoBusy,
    IReadOnlyList<AutopilotRunState> Runs,
    IReadOnlyList<AutopilotPlan> Queue,
    IReadOnlyList<AutopilotRunRecord> History,
    string? AwaitingEpicGo);

// One active run as the workspace draws it; the panes are the sessions the UI part places by id.
internal sealed record AutopilotRunState(
    string RunId,
    AutopilotPlanPhase Phase,
    AutopilotPlan? Plan,
    string? PendingQuestion,
    string? StepPaneId,
    string? CeoPaneId,
    bool IsValidating,
    bool AwaitingMergeGo);
