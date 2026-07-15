using FluentAssertions;

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
        CodexSandbox.ToPolicyType(mode).Should().Be(expected);

    // "readOnly" is the already-camelCase form — passing it back in is not a kebab choice, so it maps to null: the
    // mapper only accepts the operator-facing kebab vocabulary, and anything else drops the override rather than
    // sending a type Codex would reject.
    [Theory]
    [InlineData("readOnly")]
    [InlineData("not-a-real-mode")]
    [InlineData("")]
    [InlineData(null)]
    public void ToPolicyType_ReturnsNull_ForAnUnknownOrBlankMode(string? mode) =>
        CodexSandbox.ToPolicyType(mode).Should().BeNull();

    [Fact]
    public void Choices_AreTheThreeKebabSandboxModes() =>
        CodexSandbox.Choices.Should().Equal("read-only", "workspace-write", "danger-full-access");
}
