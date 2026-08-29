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

        Assert.Equal(UserProfileDir, environment["USERPROFILE"]);
        Assert.Equal(@"C:\Windows;C:\Windows\System32", environment["PATH"]);
        Assert.Equal(@"C:\Users\raymo\AppData\Roaming", environment["APPDATA"]);
    }

    [Fact]
    public void BuildBase_AlwaysSetsTermToXtermForTheInkTui()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        Assert.Equal("xterm-256color", environment["TERM"]);
    }

    [Fact]
    public void BuildBase_WhenNoUtf8Locale_ForcesAUtf8LocaleSoTheTuiMeasuresWidthsCorrectly()
    {
        var noUtf8 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LANG"] = "C" };

        var environment = TtyEnvironment.BuildBase(noUtf8);

        Assert.Equal("C.UTF-8", environment["LC_ALL"]);
        Assert.Equal("C.UTF-8", environment["LANG"]);
    }

    [Fact]
    public void BuildBase_WhenNoLocaleAtAll_StillForcesUtf8()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        Assert.Equal("C.UTF-8", environment["LC_ALL"]);
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
        Assert.Equal(value, environment[key]);
        Assert.NotEqual("C.UTF-8", environment.GetValueOrDefault("LC_ALL"));
    }

    [Fact]
    public void BuildBase_NeverIntroducesAnAnthropicCredentialThatWasNotThereToBeginWith()
    {
        var environment = TtyEnvironment.BuildBase(BaseEnvironment);

        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
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

        Assert.False(environment.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.False(environment.ContainsKey("CLAUDECODE"));
        Assert.False(environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.False(environment.ContainsKey("CLAUDE_AGENT_SDK_VERSION"));
        Assert.Equal(@"C:\Windows", environment["PATH"]);
    }

    [Fact]
    public void BuildBase_IsCaseInsensitive_SoTermOverwritesADifferentlyCasedInheritedValue()
    {
        var baseWithLowerTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["term"] = "dumb",
        };

        var environment = TtyEnvironment.BuildBase(baseWithLowerTerm);

        Assert.Equal("xterm-256color", environment["TERM"]);
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

        Assert.False(environment.ContainsKey("TERM_PROGRAM"));
        Assert.False(environment.ContainsKey("TERM_PROGRAM_VERSION"));
        Assert.False(environment.ContainsKey("GHOSTTY_RESOURCES_DIR"));
        Assert.False(environment.ContainsKey("GHOSTTY_BIN_DIR"));
        Assert.Equal("xterm-256color", environment["TERM"]);
        Assert.Equal("truecolor", environment["COLORTERM"]);
        Assert.Equal(@"C:\Windows", environment["PATH"]);
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

        Assert.DoesNotContain(variable, environment);
        Assert.Contains("PATH", environment);
    }

    [Fact]
    public void Compose_WithAValueInTheOverlay_SetsIt()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);
        var overlay = new Dictionary<string, string?> { ["CUSTOM_VAR"] = "custom-value" };

        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        Assert.Equal("custom-value", environment["CUSTOM_VAR"]);
        Assert.Equal("xterm-256color", environment["TERM"]);
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

        Assert.False(environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void Compose_WithoutAMatchingOverlayKey_LeavesTheBaseValueUntouched()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var environment = TtyEnvironment.Compose(baseEnvironment, new Dictionary<string, string?>());

        Assert.Equal(UserProfileDir, environment["USERPROFILE"]);
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
        Assert.Contains("CLAUDE_CONFIG_DIR", baseEnvironment);

        var overlay = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = null };
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", environment);
        Assert.Contains("PATH", environment);
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

        Assert.Equal(new[] { "ANTHROPIC_API_KEY" }, rejected);
    }

    [Fact]
    public void RejectedOverlayKeys_WithAnOverlayRemovingAHostControlledVariable_DoesNotNameIt()
    {
        var overlay = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };

        var rejected = TtyEnvironment.RejectedOverlayKeys(overlay);

        Assert.Empty(rejected);
    }
}
