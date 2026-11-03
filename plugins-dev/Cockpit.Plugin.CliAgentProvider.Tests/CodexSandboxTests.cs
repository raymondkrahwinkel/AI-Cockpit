namespace Cockpit.Plugin.CliAgentProvider.Tests;

// `CodexSandbox` (#45 D4 inc2b) — the single source of the sandbox choices and the kebab→camelCase
// mapping the live sandbox control turns into the app-server's SandboxPolicy `type` discriminator.
public class CodexSandboxTests
{
    // "readOnly" is the already-camelCase form — passing it back in is not a kebab choice, so it maps to null: the
    // mapper only accepts the operator-facing kebab vocabulary, and anything else drops the override rather than
    // sending a type Codex would reject.
    [Theory]
    [InlineData("read-only", "readOnly")]
    [InlineData("workspace-write", "workspaceWrite")]
    [InlineData("danger-full-access", "dangerFullAccess")]
    [InlineData("readOnly", null)]
    [InlineData("not-a-real-mode", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ToPolicyType_MapsEachKebabChoice_ToItsCamelCaseDiscriminator(string? mode, string? expected) =>
        Assert.Equal(expected, CodexSandbox.ToPolicyType(mode));

    // ForCeiling (AC-112): a delegated Codex task's sandbox derived from its permission ceiling. A ceiling that
    // allows edits becomes workspace-write so the task can actually write; anything less stays null so the
    // profile's configured default (read-only) holds. danger-full-access is never derived.
    [Theory]
    [InlineData("acceptEdits", "workspace-write")]
    [InlineData("bypassPermissions", "workspace-write")]
    [InlineData("plan", null)]
    [InlineData("default", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("something-invented", null)]
    public void ForCeiling_ACeilingThatAllowsEdits_IsWorkspaceWrite(string? ceiling, string? expected) =>
        Assert.Equal(expected, CodexSandbox.ForCeiling(ceiling));
}
