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

    // AC-939: Claude reports an upstream API failure (rate limit, 529 overload, …) as a *successful* result whose
    // content is the error — `subtype: "success"`, `is_error: true`, and the failure text in `result`, never in
    // `errors[]`. Without this fallback the host had no reason to show at all for exactly this failure mode.
    [Fact]
    public void ApiErrorResult_WithNoErrorsArray_FallsBackToTheResultText()
    {
        const string line = """
        {"type":"result","subtype":"success","is_error":true,"result":"API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}","session_id":"abc"}
        """;

        var completed = Assert.IsType<PluginTurnCompleted>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.True(completed.IsError);
        Assert.Equal("success", completed.Subtype);
        Assert.Contains("529", Assert.Single(completed.Errors!));
    }

    [Fact]
    public void ErrorResult_WithNoErrorsArrayAndNoResult_StaysNull()
    {
        // is_error with neither errors[] nor a string result: nothing to fall back to.
        const string line = """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"abc"}""";

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
        var withoutModel = Assert.IsType<PluginSessionInitialized>(Assert.Single(ClaudeStreamJson.ParseLine(
            """{"type":"system","subtype":"init","session_id":"abc","cwd":"/work","tools":[]}""")));
        Assert.Null(withoutModel.Model);
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
        Assert.All(changed.Tasks, task =>
        {
            Assert.Null(task.LastTool);
            Assert.Null(task.Tokens);
        });
        Assert.Equal(new PluginBackgroundTask("a0ce6840b9", PluginBackgroundTaskKind.SubAgent, "Agent 1: sleep and reply"), changed.Tasks[0]);
        Assert.Equal(new PluginBackgroundTask("b706pro1i", PluginBackgroundTaskKind.Shell, "npm run dev"), changed.Tasks[1]);
    }

    private static void _AssertEmptyBackgroundList()
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
        _AssertEmptyBackgroundList();
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
        Assert.Empty(ClaudeStreamJson.ParseLine("""{"type":"system","subtype":"hook_started","session_id":"abc"}"""));
        Assert.Empty(ClaudeStreamJson.ParseLine("""{"type":"system","subtype":"task_progress","session_id":"abc"}"""));
    }

    [Fact]
    public void TaskLifecycle_Captured314Lines_KeepIdentityAndRejectLateUpdates()
    {
        var mapper = new ClaudeStreamJson();
        var started = _Tasks(mapper, """{"type":"system","subtype":"task_started","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","tool_use_id":"toolu_01JQC5fVX21zsh9nYsHGthVu","description":"Echo protocol check","task_type":"local_agent","subagent_type":"general-purpose"}""");
        var first = Assert.Single(started.Tasks);
        Assert.Equal("a6f9e61056d8b3a4b", first.AgentId);
        Assert.Equal("0mvvfau5u-59e932a3", first.RunId);
        Assert.Equal("toolu_01JQC5fVX21zsh9nYsHGthVu", first.ParentToolUseId);
        var progress = _Tasks(mapper, """{"type":"system","subtype":"task_progress","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","description":"Running Echo CHILD_OK marker","usage":{"total_tokens":5798,"tool_uses":1,"duration_ms":1294},"last_tool_name":"Bash"}""");
        Assert.Equal(5798, Assert.Single(progress.Tasks).Tokens);
        Assert.Equal("Bash", Assert.Single(progress.Tasks).LastTool);
        Assert.Equal("general-purpose", Assert.Single(progress.Children).AgentType);
        Assert.Equal(1294, Assert.Single(progress.Children).DurationMilliseconds);
        var other = _Tasks(mapper, """{"type":"system","subtype":"task_progress","task_id":"a6f9e61056d8b3a4b","agent_id":"other","run_id":"other-run","parent_task_id":"parent","last_tool_name":"Read"}""");
        Assert.Equal(2, other.Tasks.Count);
        Assert.Equal("parent", other.Tasks[1].ParentTaskId);
        Assert.Equal("Bash", other.Tasks[0].LastTool);
        var ended = _Tasks(mapper, """{"type":"system","subtype":"task_updated","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","patch":{"status":"completed","end_time":1793392016590}}""");
        Assert.Equal("other", Assert.Single(ended.Tasks).AgentId);
        Assert.Equal("completed", Assert.Single(ended.Children, child => child.AgentId == first.AgentId).State);
        Assert.Equal(2, ended.Children.Count);
        var final = Assert.Single(mapper.Parse("""{"type":"system","subtype":"task_notification","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","status":"completed","usage":{"duration_ms":2718,"total_tokens":6447}}""").OfType<PluginBackgroundTasksChanged>());
        var finished = Assert.Single(final.Children, child => child.AgentId == first.AgentId);
        Assert.Equal("general-purpose", finished.AgentType);
        Assert.Equal(2718, finished.DurationMilliseconds);
        Assert.Equal(6447, finished.Tokens);
        Assert.Null(Assert.Single(final.Children, child => child.AgentId == "other").DurationMilliseconds);
        Assert.Empty(mapper.Parse("""{"type":"system","subtype":"task_updated","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","patch":{"status":"running"}}"""));
        var resumed = _Tasks(mapper, """{"type":"system","subtype":"task_started","task_id":"new-tool-task","agent_id":"a6f9e61056d8b3a4b","run_id":"resume","tool_use_id":"new-tool"}""");
        var sameAgent = Assert.Single(resumed.Tasks, task => task.AgentId == first.AgentId);
        Assert.Equal(first.TaskId, sameAgent.TaskId);
        Assert.Equal("resume", sameAgent.RunId);
        Assert.Empty(mapper.Parse("""{"type":"system","subtype":"task_notification","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","status":"completed"}"""));
        Assert.Empty(_Tasks(mapper, """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""").Tasks);
        Assert.Empty(mapper.Parse("""{"type":"system","subtype":"task_updated","agent_id":"a6f9e61056d8b3a4b","run_id":"resume","patch":{"status":"running"}}"""));
        Assert.Empty(mapper.Parse("""{"type":"system","subtype":"hook_started"}"""));
    }

    private static PluginBackgroundTasksChanged _Tasks(ClaudeStreamJson mapper, string line) =>
        Assert.IsType<PluginBackgroundTasksChanged>(Assert.Single(mapper.Parse(line)));

    // AC-1057: the CLI's afterthought on a backgrounded task, captured verbatim against a real 2.1.246 run driving
    // a deliberately failing `Bash(run_in_background: true)` call. The ticket assumed a plain-text
    // `<task-notification>` block in a `user` message; re-measuring found this structured `system` event instead.
    [Fact]
    public void TaskNotification_CarriesTheOutcome_KeyedToItsToolUse()
    {
        const string line = """
        {"type":"system","subtype":"task_notification","task_id":"b2n9en4yr","tool_use_id":"toolu_01ChT55cdpf1a6BRZ3EYezZz","status":"failed","output_file":"/tmp/claude-1000/x/tasks/b2n9en4yr.output","summary":"Background command \"Run false command in background\" failed with exit code 1","uuid":"40fd7fd1-606b-4b23-976d-8bf0db9f457c","session_id":"1a88bad1-2e4b-43a3-83b2-a55937fe7028"}
        """;

        var notification = Assert.IsType<PluginBackgroundTaskNotification>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal("b2n9en4yr", notification.TaskId);
        Assert.Equal("toolu_01ChT55cdpf1a6BRZ3EYezZz", notification.ToolUseId);
        Assert.Equal(PluginBackgroundTaskStatus.Failed, notification.Status);
    }

    [Fact]
    public void TaskNotification_WithAnUnknownStatus_KeepsItUnknown()
    {
        // A status a newer CLI names and this build does not must not be read as "completed" — that is exactly the
        // silent-success misreading criterion 4 rules out, same guarantee `_ParseBackgroundTasks` already gives an
        // unrecognised task_type.
        const string line = """
        {"type":"system","subtype":"task_notification","task_id":"x1","tool_use_id":"toolu_x","status":"cancelled"}
        """;

        var notification = Assert.IsType<PluginBackgroundTaskNotification>(Assert.Single(ClaudeStreamJson.ParseLine(line)));

        Assert.Equal(PluginBackgroundTaskStatus.Unknown, notification.Status);
        Assert.Empty(ClaudeStreamJson.ParseLine("""{"type":"system","subtype":"task_notification","status":"completed"}"""));
    }
}
