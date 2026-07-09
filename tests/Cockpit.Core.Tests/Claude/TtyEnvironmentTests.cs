using FluentAssertions;
using Cockpit.Core.Claude.Tty;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises the pure TTY-mode environment composer: a ConPTY child inherits nothing, so the block
/// must start from the parent env and add TERM (and CLAUDE_CONFIG_DIR under a profile).
/// </summary>
public class TtyEnvironmentTests
{
    private static readonly Dictionary<string, string> BaseEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\raymo",
        ["PATH"] = @"C:\Windows;C:\Windows\System32",
        ["APPDATA"] = @"C:\Users\raymo\AppData\Roaming",
    };

    [Fact]
    public void Build_CarriesEveryInheritedVariable()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null);

        environment["USERPROFILE"].Should().Be(@"C:\Users\raymo");
        environment["PATH"].Should().Be(@"C:\Windows;C:\Windows\System32");
        environment["APPDATA"].Should().Be(@"C:\Users\raymo\AppData\Roaming");
    }

    [Fact]
    public void Build_AlwaysSetsTermToXtermForTheInkTui()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null);

        environment["TERM"].Should().Be("xterm-256color");
    }

    [Fact]
    public void Build_WithProfile_SetsClaudeConfigDirToTheProfileDirectory()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        var environment = TtyEnvironment.Build(BaseEnvironment, profile);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Build_WithoutProfile_DoesNotSetClaudeConfigDir()
    {
        var environment = TtyEnvironment.Build(BaseEnvironment, profile: null);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void Build_NeverSetsAnthropicApiKey_KeepingTheSubscriptionRoute()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        var environment = TtyEnvironment.Build(BaseEnvironment, profile);

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

        var environment = TtyEnvironment.Build(baseWithMarkers, profile: null);

        environment.ContainsKey("CLAUDE_CODE_SESSION_ID").Should().BeFalse();
        environment.ContainsKey("CLAUDECODE").Should().BeFalse();
        environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT").Should().BeFalse();
        environment.ContainsKey("CLAUDE_AGENT_SDK_VERSION").Should().BeFalse();
        environment["PATH"].Should().Be(@"C:\Windows");
    }

    [Fact]
    public void Build_KeepsClaudeConfigDir_WhichIsNotANestedSessionMarker()
    {
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };

        var environment = TtyEnvironment.Build(baseWithConfigDir, profile);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Build_IsCaseInsensitive_SoTermOverwritesADifferentlyCasedInheritedValue()
    {
        var baseWithLowerTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["term"] = "dumb",
        };

        var environment = TtyEnvironment.Build(baseWithLowerTerm, profile: null);

        environment["TERM"].Should().Be("xterm-256color");
    }
}
