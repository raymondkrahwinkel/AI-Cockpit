using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// The managed-CLI resolution seam (AC-20) on <see cref="CliExecutableLocator"/>: a cockpit-managed install sits
/// between a pinned absolute path and PATH — a pin wins, a managed copy beats PATH, and no managed copy falls
/// through to PATH untouched.
/// </summary>
public class CliExecutableLocatorManagedTests
{
    private static readonly string ManagedPath = Path.Combine(Path.GetTempPath(), "managed", "codex");

    [Fact]
    public void RootedPin_WinsOverAManagedCopy()
    {
        var pin = Path.Combine(Path.GetTempPath(), "pinned-codex");

        CliExecutableLocator.Resolve(pin, _ => ManagedPath).Should().Be(pin);
    }

    [Fact]
    public void BareName_WithManagedCopy_ResolvesToTheManagedCopy_NotPath()
    {
        CliExecutableLocator.Resolve("codex", name => name == "codex" ? ManagedPath : null)
            .Should().Be(ManagedPath);
    }

    [Fact]
    public void BareName_NoManagedCopy_FallsThroughToPath()
    {
        const string absent = "codex-definitely-not-installed-xyz";

        CliExecutableLocator.Resolve(absent, _ => null).Should().Be(absent);
    }

    [Fact]
    public void NoResolver_BehavesExactlyAsBefore()
    {
        const string absent = "codex-definitely-not-installed-xyz";

        CliExecutableLocator.Resolve(absent).Should().Be(absent);
    }
}
