using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeExecutableLocator"/> (Fase 4) — resolving the <c>claude</c> command to a spawnable path so a bare
/// name finds the Windows <c>.cmd</c> npm shim that <see cref="System.Diagnostics.Process"/> would not. Only the
/// OS-independent contract is asserted here (a real PATH probe is environment-specific); the Windows shim probing
/// mirrors the proven Codex locator.
/// </summary>
public class ClaudeExecutableLocatorTests
{
    [Fact]
    public void Resolve_RootedPath_PassesThroughUnchanged()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "claude-does-not-need-lookup");

        ClaudeExecutableLocator.Resolve(rooted).Should().Be(rooted);
    }

    [Fact]
    public void Resolve_BareNameNotOnPath_ReturnsItUnchanged_SoStartStillGetsARealAttempt()
    {
        // A name PATH cannot resolve is returned as-is, so Process.Start makes a real attempt and yields a diagnosable
        // "file not found" rather than this resolver swallowing it.
        ClaudeExecutableLocator.Resolve("claude-provider-definitely-not-installed-xyz").Should().Be("claude-provider-definitely-not-installed-xyz");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankCommand_PassesThroughUnchanged(string command)
    {
        ClaudeExecutableLocator.Resolve(command).Should().Be(command);
    }
}
