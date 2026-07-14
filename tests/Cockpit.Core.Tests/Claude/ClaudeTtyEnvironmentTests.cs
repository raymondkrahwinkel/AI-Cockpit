using FluentAssertions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// What <c>claude</c> needs on top of the host's base environment: <c>CLAUDE_CONFIG_DIR</c> is exported
/// only for a profile on a non-default directory; a profile pinned to the CLI's default directory clears
/// it (a <see langword="null"/> overlay value) so the CLI uses its native home-root config. Asserted
/// against the composed end result (<see cref="TtyEnvironment.Compose"/> over <see cref="TtyEnvironment.BuildBase"/>)
/// where that is the more honest way to state the behaviour — a bare overlay dictionary does not show
/// whether an inherited value actually gets removed.
/// </summary>
public class ClaudeTtyEnvironmentTests
{
    private const string UserProfileDir = @"C:\Users\raymo";

    private static readonly Dictionary<string, string> BaseEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = UserProfileDir,
        ["PATH"] = @"C:\Windows;C:\Windows\System32",
    };

    [Fact]
    public void BuildOverlay_WithNonDefaultDirProfile_SetsClaudeConfigDirToTheProfileDirectory()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile, UserProfileDir);
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void BuildOverlay_WithDefaultDirProfile_DoesNotSetClaudeConfigDir_SoTheCliUsesItsNativeHomeRootConfig()
    {
        var defaultProfile = new SessionProfile("default", new ClaudeConfig(Path.Combine(UserProfileDir, ".claude")));
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, defaultProfile, UserProfileDir);
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void BuildOverlay_WithDefaultDirProfile_DropsAnInheritedClaudeConfigDir()
    {
        var defaultProfile = new SessionProfile("default", new ClaudeConfig(Path.Combine(UserProfileDir, ".claude")));
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(baseWithConfigDir);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, defaultProfile, UserProfileDir);
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
    }

    [Fact]
    public void BuildOverlay_WithoutProfile_ReturnsAnEmptyOverlay()
    {
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile: null, UserProfileDir);

        overlay.Should().BeEmpty();
    }

    [Fact]
    public void BuildOverlay_WithoutProfile_LeavesAnInheritedClaudeConfigDirUntouched_SoTheTailerCanResolveIt()
    {
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(baseWithConfigDir);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile: null, UserProfileDir);
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\some\other");
    }

    [Fact]
    public void BuildOverlay_KeepsANonDefaultConfigDir_OverAnInheritedOne()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var baseWithConfigDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\some\other",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(baseWithConfigDir);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile, UserProfileDir);
        var environment = TtyEnvironment.Compose(baseEnvironment, overlay);

        environment["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void BuildOverlay_WithAProfileAndAMemoryLimit_AddsTheNodeHeapCapOnTopOfAnyInheritedNodeOptions()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"), MemoryLimitMb: 1024);
        var baseWithNodeOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NODE_OPTIONS"] = "--enable-source-maps",
        };
        var baseEnvironment = TtyEnvironment.BuildBase(baseWithNodeOptions);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile, UserProfileDir);

        overlay["NODE_OPTIONS"].Should().Be("--enable-source-maps --max-old-space-size=1024");
    }

    [Fact]
    public void BuildOverlay_WithAProfileAndNoMemoryLimit_LeavesNodeOptionsOutOfTheOverlay()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var baseEnvironment = TtyEnvironment.BuildBase(BaseEnvironment);

        var overlay = ClaudeTtyEnvironment.BuildOverlay(baseEnvironment, profile, UserProfileDir);

        overlay.ContainsKey("NODE_OPTIONS").Should().BeFalse();
    }
}
