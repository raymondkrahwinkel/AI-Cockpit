using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Exercises the pure TTY-mode environment composer every provider shares: a ConPTY child inherits
/// nothing, so the base block must start from the parent env, add TERM/a UTF-8 locale, and strip what no
/// provider gets to keep (nested-agent markers, the host terminal's identity, an inherited Anthropic
/// credential). What a provider adds on top is <see cref="TtyEnvironment.Compose"/>'s job, covered here
/// against a synthetic overlay rather than a Claude-shaped one.
/// </summary>
public class TtyEnvironmentTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("anthropic_api_key")]
    [InlineData("COCKPIT_CONNECT_KEY")]
    [InlineData("COCKPIT_CONNECT_KEY_FILE")]
    public void BuildBase_DropsAnInheritedCredential(string variable)
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            [variable] = "a-key-the-shell-exported",
        };

        var environment = TtyEnvironment.BuildBase(inherited);

        Assert.DoesNotContain(variable, environment);
        Assert.Contains("PATH", environment);
    }

    // A provider cannot reinstate what the host stripped: an overlay entry for a host-controlled key
    // (IsHostControlled — nested-agent markers, host terminal identity, any ANTHROPIC_* credential) is
    // ignored unless it removes the key. Otherwise the scrub would be advisory, and a provider could hand
    // the child a credential the operator never chose just by asking for it in its own overlay.
    [Fact]
    public void Compose_WithAProviderOverlayTryingToSetAHostControlledVariable_IgnoresIt()
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = "inherited-from-the-shell",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(inherited);
        Assert.False(baseEnvironment.ContainsKey("ANTHROPIC_API_KEY"), "BuildBase already stripped it");

        var overlay = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "set-deliberately-by-the-provider" };
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"), "a provider does not get to put back what the host stripped");
    }
}
