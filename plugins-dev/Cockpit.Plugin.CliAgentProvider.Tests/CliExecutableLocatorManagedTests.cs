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

        Assert.Equal(pin, CliExecutableLocator.Resolve(pin, _ => ManagedPath));
    }

    [Fact]
    public void BareName_WithManagedCopy_ResolvesToTheManagedCopy_NotPath()
    {
        Assert.Equal(ManagedPath, CliExecutableLocator.Resolve("codex", name => name == "codex" ? ManagedPath : null));
    }

    [Fact]
    public void BareName_NoManagedCopy_FallsThroughToPath()
    {
        const string absent = "codex-definitely-not-installed-xyz";

        Assert.Equal(absent, CliExecutableLocator.Resolve(absent, _ => null));
    }

    [Fact]
    public void NoResolver_BehavesExactlyAsBefore()
    {
        const string absent = "codex-definitely-not-installed-xyz";

        Assert.Equal(absent, CliExecutableLocator.Resolve(absent));
    }
}
