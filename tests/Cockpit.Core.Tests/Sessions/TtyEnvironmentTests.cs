using FluentAssertions;
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
    public void BuildBase_CarriesEveryInheritedVariable()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        environment["USERPROFILE"].Should().Be(UserProfileDir);
        environment["PATH"].Should().Be(@"C:\Windows;C:\Windows\System32");
        environment["APPDATA"].Should().Be(@"C:\Users\raymo\AppData\Roaming");
    }

    [Fact]
    public void BuildBase_AlwaysSetsTermToXtermForTheInkTui()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        environment["TERM"].Should().Be("xterm-256color");
    }

    [Fact]
    public void BuildBase_WhenNoUtf8Locale_ForcesAUtf8LocaleSoTheTuiMeasuresWidthsCorrectly()
    {
        var noUtf8 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LANG"] = "C" };

        var environment = TtyEnvironment.BuildBase(noUtf8);

        environment["LC_ALL"].Should().Be("C.UTF-8");
        environment["LANG"].Should().Be("C.UTF-8");
    }

    [Fact]
    public void BuildBase_WhenNoLocaleAtAll_StillForcesUtf8()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        environment["LC_ALL"].Should().Be("C.UTF-8");
    }

    [Theory]
    [InlineData("LANG", "en_US.UTF-8")]
    [InlineData("LC_ALL", "nl_NL.UTF-8")]
    [InlineData("LC_CTYPE", "en_GB.utf8")]
    public void BuildBase_WhenAUtf8LocaleIsAlreadyPresent_LeavesItUntouched(string key, string value)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value };

        var environment = TtyEnvironment.BuildBase(env);

        // The already-working UTF-8 locale is preserved, and no C.UTF-8 fallback was forced over it.
        environment[key].Should().Be(value);
        environment.GetValueOrDefault("LC_ALL").Should().NotBe("C.UTF-8");
    }

    [Fact]
    public void BuildBase_NeverIntroducesAnAnthropicCredentialThatWasNotThereToBeginWith()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        environment.ContainsKey("ANTHROPIC_API_KEY").Should().BeFalse();
    }

    [Fact]
    public void BuildBase_StripsTheNestedClaudeCodeSessionMarkers_SoTheChildDoesNotAdoptTheLaunchersSession()
    {
        var baseWithMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CODE_SESSION_ID"] = "11111111-1111-1111-1111-111111111111",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop",
            ["CLAUDE_AGENT_SDK_VERSION"] = "0.3.0",
            ["PATH"] = @"C:\Windows",
        };

        var environment = TtyEnvironment.BuildBase(baseWithMarkers);

        environment.ContainsKey("CLAUDE_CODE_SESSION_ID").Should().BeFalse();
        environment.ContainsKey("CLAUDECODE").Should().BeFalse();
        environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT").Should().BeFalse();
        environment.ContainsKey("CLAUDE_AGENT_SDK_VERSION").Should().BeFalse();
        environment["PATH"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void BuildBase_IsCaseInsensitive_SoTermOverwritesADifferentlyCasedInheritedValue()
    {
        var baseWithLowerTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["term"] = "dumb",
        };

        var environment = TtyEnvironment.BuildBase(baseWithLowerTerm);

        environment["TERM"].Should().Be("xterm-256color");
    }

    [Fact]
    public void BuildBase_StripsTheHostTerminalIdentityMarkers_SoTheChildDoesNotDetectGhosttyAndDesyncItsRenderPath()
    {
        var baseWithGhosttyMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM_PROGRAM"] = "ghostty",
            ["TERM_PROGRAM_VERSION"] = "1.2.3",
            ["GHOSTTY_RESOURCES_DIR"] = @"C:\Program Files\Ghostty\resources",
            ["GHOSTTY_BIN_DIR"] = @"C:\Program Files\Ghostty\bin",
            ["COLORTERM"] = "truecolor",
            ["PATH"] = @"C:\Windows",
        };

        var environment = TtyEnvironment.BuildBase(baseWithGhosttyMarkers);

        environment.ContainsKey("TERM_PROGRAM").Should().BeFalse();
        environment.ContainsKey("TERM_PROGRAM_VERSION").Should().BeFalse();
        environment.ContainsKey("GHOSTTY_RESOURCES_DIR").Should().BeFalse();
        environment.ContainsKey("GHOSTTY_BIN_DIR").Should().BeFalse();
        environment["TERM"].Should().Be("xterm-256color");
        environment["COLORTERM"].Should().Be("truecolor");
        environment["PATH"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void BuildBase_WithoutHostTerminalIdentityMarkers_LeavesUnrelatedVariablesUntouched()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        environment["USERPROFILE"].Should().Be(UserProfileDir);
        environment["PATH"].Should().Be(@"C:\Windows;C:\Windows\System32");
        environment["APPDATA"].Should().Be(@"C:\Users\raymo\AppData\Roaming");
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("anthropic_api_key")]
    public void BuildBase_DropsAnInheritedAnthropicCredential(string variable)
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            [variable] = "a-key-the-shell-exported",
        };

        var environment = TtyEnvironment.BuildBase(inherited);

        environment.Should().NotContainKey(variable);
        environment.Should().ContainKey("PATH", "only the credential is dropped, not the rest of the environment");
    }

    [Fact]
    public void Compose_WithAValueInTheOverlay_SetsIt()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);
        var overlay = new Dictionary<string, string?> { ["CUSTOM_VAR"] = "custom-value" };

        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment["CUSTOM_VAR"].Should().Be("custom-value");
        environment["TERM"].Should().Be("xterm-256color", "the base is still there, not replaced by the overlay");
    }

    [Fact]
    public void Compose_WithANullValueInTheOverlay_RemovesTheKeyFromTheBase()
    {
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(baseWithConfigDir);
        var overlay = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = null };

        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void Compose_WithoutAMatchingOverlayKey_LeavesTheBaseValueUntouched()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var environment = TtyEnvironment.Compose(baseEnvironment, new Dictionary<string, string?>());

        environment["USERPROFILE"].Should().Be(UserProfileDir);
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
        baseEnvironment.ContainsKey("ANTHROPIC_API_KEY").Should().BeFalse("BuildBase already stripped it");

        var overlay = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "set-deliberately-by-the-provider" };
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment.ContainsKey("ANTHROPIC_API_KEY").Should().BeFalse("a provider does not get to put back what the host stripped");
    }

    [Fact]
    public void Compose_WithAProviderOverlayRemovingAVariable_TakesItOutOfTheBase()
    {
        // Removal has to be expressible, and not only for host-controlled keys: clearing an inherited
        // CLAUDE_CONFIG_DIR is how a default-profile session reaches the CLI's own home-root config at all
        // (the onboarding bug). Setting a host-controlled key is rejected; clearing one asks for nothing.
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            ["CLAUDE_CONFIG_DIR"] = "/home/someone/.claude-work",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(inherited);
        baseEnvironment.Should().ContainKey("CLAUDE_CONFIG_DIR", "it is not host-controlled — a provider owns it");

        var overlay = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = null };
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment.Should().NotContainKey("CLAUDE_CONFIG_DIR");
        environment.Should().ContainKey("PATH", "only what the overlay names is touched");
    }

    [Fact]
    public void RejectedOverlayKeys_WithAnOverlaySettingAHostControlledVariable_NamesIt()
    {
        var overlay = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = "set-deliberately-by-the-provider",
            ["CUSTOM_VAR"] = "harmless",
        };

        var rejected = TtyEnvironment.RejectedOverlayKeys(overlay);

        rejected.Should().Equal("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void RejectedOverlayKeys_WithAnOverlayRemovingAHostControlledVariable_DoesNotNameIt()
    {
        var overlay = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };

        var rejected = TtyEnvironment.RejectedOverlayKeys(overlay);

        rejected.Should().BeEmpty();
    }
}
