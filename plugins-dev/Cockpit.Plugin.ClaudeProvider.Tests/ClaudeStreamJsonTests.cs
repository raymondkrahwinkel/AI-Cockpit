using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeStreamJson"/>'s "result" parsing (AC-410) — specifically the <c>errors[]</c> array, which
/// <c>_ParseResult</c> did not read before this. Verified against the real CLI (2026-07-29, per the design doc):
/// <c>claude --resume &lt;unknown-id&gt; -p "…" --output-format stream-json</c> fails loud and machine-readable,
/// with <c>subtype: "error_during_execution"</c>, no <c>result</c>, and the actual reason only in <c>errors[]</c>.
/// Without reading it, the host sees that a resume failed but never why.
/// </summary>
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
}
