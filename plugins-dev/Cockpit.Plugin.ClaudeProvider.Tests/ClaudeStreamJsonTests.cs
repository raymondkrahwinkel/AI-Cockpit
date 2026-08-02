using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeStreamJson`'s "result" parsing (AC-410) — specifically the `errors[]` array, which
// `_ParseResult` did not read before this. Verified against the real CLI (2026-07-29, per the design doc):
// `claude --resume &lt;unknown-id&gt; -p "…" --output-format stream-json` fails loud and machine-readable,
// with `subtype: "error_during_execution"`, no `result`, and the actual reason only in `errors[]`.
// Without reading it, the host sees that a resume failed but never why.
public class ClaudeStreamJsonTests
{
    [Fact]
    public void ErrorDuringExecution_WithNoResult_CarriesTheErrorsArray()
    {
        const string line = """
        {"type":"result","subtype":"error_during_execution","is_error":true,"duration_ms":42,"session_id":"abc",
         "errors":["No conversation found with session ID: 00000000-dead-beef-0000-000000000000"]}
        """;

        var turn = Assert.Single(ClaudeStreamJson.ParseLine(line));
        var completed = Assert.IsType<PluginTurnCompleted>(turn);

        Assert.Equal("error_during_execution", completed.Subtype);
        Assert.True(completed.IsError);
        Assert.Null(completed.Result);
        Assert.NotNull(completed.Errors);
        Assert.Equal("No conversation found with session ID: 00000000-dead-beef-0000-000000000000", Assert.Single(completed.Errors));
    }

    [Fact]
    public void OrdinarySuccessResult_HasNoErrors()
    {
        const string line = """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"abc"}""";

        var completed = Assert.IsType<PluginTurnCompleted>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Null(completed.Errors);
    }

    [Fact]
    public void ErrorsArray_WithNonStringEntries_SkipsThemRatherThanThrowing()
    {
        const string line = """{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["real reason",42,null]}""";

        var completed = Assert.IsType<PluginTurnCompleted>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("real reason", Assert.Single(completed.Errors!));
    }

    // AC-141: the init event is the only place a session launched with no explicit model (Auto/default) states
    // which one the CLI actually resolved it to — without reading it, the host's Model live-control has nothing
    // to seed itself with and shows an empty placeholder even though effort and permission mode, which always
    // have a default, show theirs.
    [Fact]
    public void InitEvent_WithModel_CarriesItOnPluginSessionInitialized()
    {
        const string line = """
        {"type":"system","subtype":"init","session_id":"abc","cwd":"/work","tools":["Read"],
         "model":"claude-sonnet-4-5-20250929"}
        """;

        var initialized = Assert.IsType<PluginSessionInitialized>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("claude-sonnet-4-5-20250929", initialized.Model);
    }

    [Fact]
    public void InitEvent_WithNoModel_LeavesItNull()
    {
        const string line = """{"type":"system","subtype":"init","session_id":"abc","cwd":"/work","tools":[]}""";

        var initialized = Assert.IsType<PluginSessionInitialized>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Null(initialized.Model);
    }

    // AC-146: a sub-agent's own wire events carry parent_tool_use_id alongside session_id, naming the Task
    // tool_use_id that spawned them — stamped onto whatever event(s) the line yields so the host can nest them
    // under that call instead of flattening them into the top-level transcript.
    [Fact]
    public void StreamEvent_WithParentToolUseId_CarriesItOnTheTextDelta()
    {
        const string line = """
        {"type":"stream_event","session_id":"abc","parent_tool_use_id":"toolu_task1",
         "event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"looking into it"}}}
        """;

        var delta = Assert.IsType<PluginAssistantTextDelta>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("toolu_task1", delta.ParentToolUseId);
        Assert.Equal("looking into it", delta.Text);
    }

    [Fact]
    public void Assistant_WithParentToolUseId_CarriesItOnTheToolUseRequested()
    {
        const string line = """
        {"type":"assistant","session_id":"abc","parent_tool_use_id":"toolu_task1",
         "message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_sub1","name":"Read","input":{}}]}}
        """;

        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("toolu_task1", toolUse.ParentToolUseId);
        Assert.Equal("toolu_sub1", toolUse.ToolUseId);
    }

    [Fact]
    public void StreamEvent_WithNoParentToolUseId_LeavesItNull()
    {
        const string line = """
        {"type":"stream_event","session_id":"abc",
         "event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"top-level reply"}}}
        """;

        var delta = Assert.IsType<PluginAssistantTextDelta>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Null(delta.ParentToolUseId);
    }

    // AC-276: the CLI reports what has outlived the turn on system/background_tasks_changed. _ParseSystem used to
    // drop every system line that was not "init", so this reached the host as nothing at all. The lines below are
    // taken verbatim from a captured run against the real CLI in the persistent --input-format stream-json mode the
    // driver actually uses (never -p), where the task list demonstrably arrives before the turn's own result.
    [Fact]
    public void BackgroundTasksChanged_CarriesEveryTask_WithItsKind()
    {
        const string line = """
        {"type":"system","subtype":"background_tasks_changed","session_id":"abc","tasks":[
         {"task_id":"a0ce6840b9","task_type":"local_agent","description":"Agent 1: sleep and reply"},
         {"task_id":"b706pro1i","task_type":"local_bash","description":"npm run dev"}]}
        """;

        var changed = Assert.IsType<PluginBackgroundTasksChanged>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal(2, changed.Tasks.Count);
        Assert.Equal(new PluginBackgroundTask("a0ce6840b9", PluginBackgroundTaskKind.SubAgent, "Agent 1: sleep and reply"), changed.Tasks[0]);
        Assert.Equal(new PluginBackgroundTask("b706pro1i", PluginBackgroundTaskKind.Shell, "npm run dev"), changed.Tasks[1]);
    }

    [Fact]
    public void BackgroundTasksChanged_WithAnEmptyList_ReportsNothingOutstanding()
    {
        // How the last task ending arrives: not a per-task "done" but a restatement of the whole set, now empty.
        // A consumer that treated an empty list as "no news" would never let the session go idle again.
        const string line = """{"type":"system","subtype":"background_tasks_changed","session_id":"abc","tasks":[]}""";

        var changed = Assert.IsType<PluginBackgroundTasksChanged>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Empty(changed.Tasks);
    }

    [Fact]
    public void BackgroundTasksChanged_WithAnUnknownTaskType_KeepsTheTaskButNotItsKind()
    {
        // A task type a newer CLI names and this build does not must still count as outstanding work — dropping it
        // would report an idle session that is not idle. It maps to Unknown rather than guessing SubAgent, which
        // would let it freeze the status.
        const string line = """
        {"type":"system","subtype":"background_tasks_changed","session_id":"abc",
         "tasks":[{"task_id":"x1","task_type":"remote_sandbox","description":"something new"}]}
        """;

        var changed = Assert.IsType<PluginBackgroundTasksChanged>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal(PluginBackgroundTaskKind.Unknown, Assert.Single(changed.Tasks).Kind);
    }

    [Fact]
    public void BackgroundTasksChanged_SkipsATaskWithNoId()
    {
        // Ids are what make a restatement idempotent; a nameless entry would inflate the set on every event.
        const string line = """
        {"type":"system","subtype":"background_tasks_changed","session_id":"abc",
         "tasks":[{"task_type":"local_bash","description":"no id"},{"task_id":"ok","task_type":"local_bash"}]}
        """;

        var changed = Assert.IsType<PluginBackgroundTasksChanged>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("ok", Assert.Single(changed.Tasks).TaskId);
    }

    [Fact]
    public void System_WithAnUnrelatedSubtype_StillYieldsNothing()
    {
        // The other system subtypes (hook_started, thinking_tokens, task_progress, …) carry no session-level
        // signal; opening _ParseSystem up for background_tasks_changed must not start admitting those.
        Assert.Empty(ClaudeStreamJson.ParseLine("""{"type":"system","subtype":"task_progress","session_id":"abc"}"""));
    }
}
