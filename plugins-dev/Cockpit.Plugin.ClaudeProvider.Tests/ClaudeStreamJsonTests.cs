using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeStreamJson`'s "result" parsing (AC-410) — specifically the `errors[]` array, which
// `_ParseResult` did not read before this. Verified against the real CLI (2026-07-29, per the design doc):
// `claude --resume &lt;unknown-id&gt; -p "…" --output-format stream-json` fails loud and machine-readable,
// with `subtype: "error_during_execution"`, no `result`, and the actual reason only in `errors[]`.
// Without reading it, the host sees that a resume failed but never why.
public class ClaudeStreamJsonTests
{

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
    public void TaskLifecycle_Captured314Lines_KeepIdentityAndRejectLateUpdates()
    {
        var mapper = new ClaudeStreamJson();
        PluginBackgroundTasksChanged started = _Tasks(mapper, """{"type":"system","subtype":"task_started","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","tool_use_id":"toolu_01JQC5fVX21zsh9nYsHGthVu","description":"Echo protocol check","task_type":"local_agent","subagent_type":"general-purpose"}""");
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
        PluginBackgroundTasksChanged final = Assert.Single(mapper.Parse("""{"type":"system","subtype":"task_notification","task_id":"a6f9e61056d8b3a4b","run_id":"0mvvfau5u-59e932a3","status":"completed","usage":{"duration_ms":2718,"total_tokens":6447}}""").OfType<PluginBackgroundTasksChanged>());
        PluginBackgroundTask finished = Assert.Single(final.Children, child => child.AgentId == first.AgentId);
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
}
