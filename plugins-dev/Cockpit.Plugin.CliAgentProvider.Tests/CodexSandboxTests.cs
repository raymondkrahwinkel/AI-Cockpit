namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexSandbox"/> (#45 D4 inc2b) — the single source of the sandbox choices and the kebab→camelCase
/// mapping the live sandbox control turns into the app-server's SandboxPolicy <c>type</c> discriminator.
/// </summary>
public class CodexSandboxTests
{
    [Theory]
    [InlineData("read-only", "readOnly")]
    [InlineData("workspace-write", "workspaceWrite")]
    [InlineData("danger-full-access", "dangerFullAccess")]
    public void ToPolicyType_MapsEachKebabChoice_ToItsCamelCaseDiscriminator(string mode, string expected) =>
        Assert.Equal(expected, CodexSandbox.ToPolicyType(mode));

    // "readOnly" is the already-camelCase form — passing it back in is not a kebab choice, so it maps to null: the
    // mapper only accepts the operator-facing kebab vocabulary, and anything else drops the override rather than
    // sending a type Codex would reject.
    [Theory]
    [InlineData("readOnly")]
    [InlineData("not-a-real-mode")]
    [InlineData("")]
    [InlineData(null)]
    public void ToPolicyType_ReturnsNull_ForAnUnknownOrBlankMode(string? mode) =>
        Assert.Null(CodexSandbox.ToPolicyType(mode));

    [Fact]
    public void Choices_AreTheThreeKebabSandboxModes() =>
        Assert.Equal(new[] { "read-only", "workspace-write", "danger-full-access" }, CodexSandbox.Choices);

    // ForCeiling (AC-112): a delegated Codex task's sandbox derived from its permission ceiling. A ceiling that
    // allows edits becomes workspace-write so the task can actually write; anything less stays null so the
    // profile's configured default (read-only) holds. danger-full-access is never derived.
    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void ForCeiling_ACeilingThatAllowsEdits_IsWorkspaceWrite(string ceiling) =>
        Assert.Equal("workspace-write", CodexSandbox.ForCeiling(ceiling));

    [Theory]
    [InlineData("plan")]
    [InlineData("default")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-invented")]
    public void ForCeiling_AReadOnlyOrUnknownCeiling_IsNull_SoTheConfigDefaultHolds(string? ceiling) =>
        Assert.Null(CodexSandbox.ForCeiling(ceiling));

    [Fact]
    public void ForCeiling_NeverDerivesDangerFullAccess()
    {
        Assert.NotEqual("danger-full-access", CodexSandbox.ForCeiling("acceptEdits"));
        Assert.NotEqual("danger-full-access", CodexSandbox.ForCeiling("bypassPermissions"));
    }
}
