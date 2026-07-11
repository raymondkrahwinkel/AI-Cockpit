using FluentAssertions;
using Cockpit.Core.Claude.Tty;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises the pure TTY-mode environment composer: a ConPTY child inherits nothing, so the block
/// must start from the parent env and add TERM. CLAUDE_CONFIG_DIR is exported only for a profile on a
/// non-default directory; a default-dir profile clears it so the CLI keeps its native home-root config.
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
    public void Build_CarriesEveryInheritedVariable()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null, UserProfileDir);

        environment["USERPROFILE"].Should().Be(UserProfileDir);
        environment["PATH"].Should().Be(@"C:\Windows;C:\Windows\System32");
        environment["APPDATA"].Should().Be(@"C:\Users\raymo\AppData\Roaming");
    }

    [Fact]
    public void Build_AlwaysSetsTermToXtermForTheInkTui()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null, UserProfileDir);

        environment["TERM"].Should().Be("xterm-256color");
    }

    [Fact]
    public void Build_WhenNoUtf8Locale_ForcesAUtf8LocaleSoTheTuiMeasuresWidthsCorrectly()
    {
        var noUtf8 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LANG"] = "C" };

        var environment = TtyEnvironment.Build(noUtf8, profile: null, UserProfileDir);

        environment["LC_ALL"].Should().Be("C.UTF-8");
        environment["LANG"].Should().Be("C.UTF-8");
    }

    [Fact]
    public void Build_WhenNoLocaleAtAll_StillForcesUtf8()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null, UserProfileDir);

        environment["LC_ALL"].Should().Be("C.UTF-8");
    }

    [Theory]
    [InlineData("LANG", "en_US.UTF-8")]
    [InlineData("LC_ALL", "nl_NL.UTF-8")]
    [InlineData("LC_CTYPE", "en_GB.utf8")]
    public void Build_WhenAUtf8LocaleIsAlreadyPresent_LeavesItUntouched(string key, string value)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value };

        var environment = TtyEnvironment.Build(env, profile: null, UserProfileDir);

        // The already-working UTF-8 locale is preserved, and no C.UTF-8 fallback was forced over it.
        environment[key].Should().Be(value);
        environment.GetValueOrDefault("LC_ALL").Should().NotBe("C.UTF-8");
    }

    [Fact]
    public void Build_WithNonDefaultDirProfile_SetsClaudeConfigDirToTheProfileDirectory()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        var environment = TtyEnvironment.Build(BaseEnvironment, profile, UserProfileDir);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Build_WithDefaultDirProfile_DoesNotSetClaudeConfigDir_SoTheCliUsesItsNativeHomeRootConfig()
    {
        var defaultProfile = new ClaudeProfile("default", Path.Combine(UserProfileDir, ".claude"));

        var environment = TtyEnvironment.Build(BaseEnvironment, defaultProfile, UserProfileDir);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void Build_WithDefaultDirProfile_DropsAnInheritedClaudeConfigDir()
    {
        var defaultProfile = new ClaudeProfile("default", Path.Combine(UserProfileDir, ".claude"));
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };

        var environment = TtyEnvironment.Build(baseWithConfigDir, defaultProfile, UserProfileDir);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void Build_WithoutProfile_DoesNotSetClaudeConfigDir()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null, UserProfileDir);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void Build_WithoutProfile_LeavesAnInheritedClaudeConfigDirUntouched_SoTheTailerCanResolveIt()
    {
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };

        var environment = TtyEnvironment.Build(baseWithConfigDir, profile: null, UserProfileDir);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\some\other");
    }

    [Fact]
    public void Build_NeverSetsAnthropicApiKey_KeepingTheSubscriptionRoute()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        var environment = TtyEnvironment.Build(BaseEnvironment, profile, UserProfileDir);

        environment.ContainsKey("ANTHROPIC_API_KEY").Should().BeFalse();
    }

    [Fact]
    public void Build_StripsTheNestedClaudeCodeSessionMarkers_SoTheChildDoesNotAdoptTheLaunchersSession()
    {
        var baseWithMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CODE_SESSION_ID"] = "11111111-1111-1111-1111-111111111111",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop",
            ["CLAUDE_AGENT_SDK_VERSION"] = "0.3.0",
            ["PATH"] = @"C:\Windows",
        };

        var environment = TtyEnvironment.Build(baseWithMarkers, profile: null, UserProfileDir);

        environment.ContainsKey("CLAUDE_CODE_SESSION_ID").Should().BeFalse();
        environment.ContainsKey("CLAUDECODE").Should().BeFalse();
        environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT").Should().BeFalse();
        environment.ContainsKey("CLAUDE_AGENT_SDK_VERSION").Should().BeFalse();
        environment["PATH"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void Build_KeepsANonDefaultConfigDir_OverAnInheritedOne()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };

        var environment = TtyEnvironment.Build(baseWithConfigDir, profile, UserProfileDir);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Build_IsCaseInsensitive_SoTermOverwritesADifferentlyCasedInheritedValue()
    {
        var baseWithLowerTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["term"] = "dumb",
        };

        var environment = TtyEnvironment.Build(baseWithLowerTerm, profile: null, UserProfileDir);

        environment["TERM"].Should().Be("xterm-256color");
    }

    [Fact]
    public void Build_StripsTheHostTerminalIdentityMarkers_SoTheChildDoesNotDetectGhosttyAndDesyncItsRenderPath()
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

        var environment = TtyEnvironment.Build(baseWithGhosttyMarkers, profile: null, UserProfileDir);

        environment.ContainsKey("TERM_PROGRAM").Should().BeFalse();
        environment.ContainsKey("TERM_PROGRAM_VERSION").Should().BeFalse();
        environment.ContainsKey("GHOSTTY_RESOURCES_DIR").Should().BeFalse();
        environment.ContainsKey("GHOSTTY_BIN_DIR").Should().BeFalse();
        environment["TERM"].Should().Be("xterm-256color");
        environment["COLORTERM"].Should().Be("truecolor");
        environment["PATH"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void Build_WithoutHostTerminalIdentityMarkers_LeavesUnrelatedVariablesUntouched()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null, UserProfileDir);

        environment["USERPROFILE"].Should().Be(UserProfileDir);
        environment["PATH"].Should().Be(@"C:\Windows;C:\Windows\System32");
        environment["APPDATA"].Should().Be(@"C:\Users\raymo\AppData\Roaming");
    }
}
