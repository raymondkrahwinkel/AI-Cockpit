using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// The managed-CLI resolution seam (AC-20) on <see cref="ClaudeExecutableLocator"/>: a cockpit-managed install sits
/// between a pinned absolute path and PATH. Order is what these assert — a pin still wins, a managed copy beats PATH,
/// and no managed copy (offline, uninstalled) falls through to PATH untouched.
/// </summary>
public class ClaudeExecutableLocatorManagedTests
{
    private static readonly string ManagedPath = Path.Combine(Path.GetTempPath(), "managed", "claude");

    [Fact]
    public void RootedPin_WinsOverAManagedCopy()
    {
        var pin = Path.Combine(Path.GetTempPath(), "pinned-claude");

        ClaudeExecutableLocator.Resolve(pin, _ => ManagedPath).Should().Be(pin);
    }

    [Fact]
    public void BareName_WithManagedCopy_ResolvesToTheManagedCopy_NotPath()
    {
        ClaudeExecutableLocator.Resolve("claude", name => name == "claude" ? ManagedPath : null)
            .Should().Be(ManagedPath);
    }

    [Fact]
    public void BareName_NoManagedCopy_FallsThroughToPath()
    {
        // A null managed result (nothing installed / offline / removed) must not short-circuit resolution: an
        // unknown name then falls through PATH and returns unchanged, exactly as without a resolver.
        const string absent = "claude-provider-definitely-not-installed-xyz";

        ClaudeExecutableLocator.Resolve(absent, _ => null).Should().Be(absent);
    }

    [Fact]
    public void BareName_ManagedResolverReturnsEmpty_IsTreatedAsAbsent()
    {
        const string absent = "claude-provider-definitely-not-installed-xyz";

        ClaudeExecutableLocator.Resolve(absent, _ => string.Empty).Should().Be(absent);
    }

    [Fact]
    public void NoResolver_BehavesExactlyAsBefore()
    {
        const string absent = "claude-provider-definitely-not-installed-xyz";

        ClaudeExecutableLocator.Resolve(absent).Should().Be(absent);
    }
}
