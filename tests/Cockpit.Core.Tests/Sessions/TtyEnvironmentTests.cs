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
    private const string UserProfileDir = @"C:\Users\raymo";

    private static readonly Dictionary<string, string> BaseEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = UserProfileDir,
        ["PATH"] = @"C:\Windows;C:\Windows\System32",
        ["APPDATA"] = @"C:\Users\raymo\AppData\Roaming",
    };

    [Fact]
    public void BuildBase_NeverIntroducesAnAnthropicCredentialThatWasNotThereToBeginWith()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

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

    // The pane id is who a session is, not a setting it may choose (AC-13, AC-165). A profile, a provider or a
    // plugin contribution that could set it would let that session set another pane's statusline and be attributed
    // another pane's consent — so it belongs to the host the same way COCKPIT_MCP_KEY does.
    [Fact]
    public void Compose_WithAnOverlayTryingToSetThePaneId_IgnoresIt()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var overlay = new Dictionary<string, string?> { ["COCKPIT_PANE_ID"] = "someone-elses-pane" };

        Assert.False(
            TtyEnvironment.Compose(baseEnvironment, overlay).ContainsKey("COCKPIT_PANE_ID"),
            "nothing but the host gets to say which pane a session is");
    }

    [Fact]
    public void BuildBase_DropsAnInheritedPaneId()
    {
        // A cockpit launched from inside a cockpit session would otherwise hand its child the parent pane's identity,
        // and the child would report its status as the parent.
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COCKPIT_PANE_ID"] = "the-parent-pane",
        };

        Assert.False(TtyEnvironment.BuildBase(inherited).ContainsKey("COCKPIT_PANE_ID"));
    }

}
