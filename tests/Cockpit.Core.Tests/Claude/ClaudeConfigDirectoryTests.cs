using FluentAssertions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises the config-directory resolver the transcript tailers use: a profile pins its own dir,
/// while a profile-less session falls back to CLAUDE_CONFIG_DIR or the CLI default ~/.claude — the
/// gap that used to silence read-aloud and status on the default (no-profile) TTY session.
/// </summary>
public class ClaudeConfigDirectoryTests
{
    [Fact]
    public void Resolve_WithProfile_ReturnsTheProfileDirectory()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));

        var resolved = ClaudeConfigDirectory.Resolve(profile.Claude, @"C:\ignored", @"C:\Users\raymo");

        resolved.Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Resolve_WithoutProfile_AndEnvSet_ReturnsTheEnvDirectory()
    {
        var resolved = ClaudeConfigDirectory.Resolve(claude: null, @"C:\Users\raymo\.claude-alt", @"C:\Users\raymo");

        resolved.Should().Be(@"C:\Users\raymo\.claude-alt");
    }

    [Fact]
    public void Resolve_WithoutProfile_AndNoEnv_FallsBackToUserProfileDotClaude()
    {
        var resolved = ClaudeConfigDirectory.Resolve(claude: null, environmentConfigDir: null, @"C:\Users\raymo");

        resolved.Should().Be(Path.Combine(@"C:\Users\raymo", ".claude"));
    }

    [Fact]
    public void Resolve_WithoutProfile_AndBlankEnv_FallsBackToUserProfileDotClaude()
    {
        var resolved = ClaudeConfigDirectory.Resolve(claude: null, environmentConfigDir: "   ", @"C:\Users\raymo");

        resolved.Should().Be(Path.Combine(@"C:\Users\raymo", ".claude"));
    }

    [Fact]
    public void ResolveSpawnOverride_WithoutProfile_ReturnsNull()
    {
        ClaudeConfigDirectory.ResolveSpawnOverride(claude: null, @"C:\Users\raymo").Should().BeNull();
    }

    [Fact]
    public void ResolveSpawnOverride_WithNonDefaultDirProfile_ReturnsThatDirectory()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(Path.Combine(@"C:\Users\raymo", ".claude-work")));

        ClaudeConfigDirectory.ResolveSpawnOverride(profile.Claude, @"C:\Users\raymo")
            .Should().Be(Path.Combine(@"C:\Users\raymo", ".claude-work"));
    }

    [Fact]
    public void ResolveSpawnOverride_WithDefaultDirProfile_ReturnsNull_SoTheCliKeepsItsHomeRootConfig()
    {
        var profile = new SessionProfile("default", new ClaudeConfig(Path.Combine(@"C:\Users\raymo", ".claude")));

        ClaudeConfigDirectory.ResolveSpawnOverride(profile.Claude, @"C:\Users\raymo").Should().BeNull();
    }

    [Fact]
    public void IsDefaultDirectory_IgnoresATrailingSeparator()
    {
        var withTrailingSeparator = Path.Combine(@"C:\Users\raymo", ".claude") + Path.DirectorySeparatorChar;

        ClaudeConfigDirectory.IsDefaultDirectory(withTrailingSeparator, @"C:\Users\raymo").Should().BeTrue();
    }

    [Fact]
    public void IsDefaultDirectory_IsFalseForANonDefaultDir()
    {
        ClaudeConfigDirectory.IsDefaultDirectory(Path.Combine(@"C:\Users\raymo", ".claude-work"), @"C:\Users\raymo")
            .Should().BeFalse();
    }

    [Fact]
    public void ResolveConfigJsonDirectory_WithNonDefaultDirProfile_ReturnsTheProfileDirectory()
    {
        var profile = new SessionProfile("work", new ClaudeConfig(Path.Combine(@"C:\Users\raymo", ".claude-work")));

        ClaudeConfigDirectory.ResolveConfigJsonDirectory(profile.Claude, @"C:\Users\raymo")
            .Should().Be(Path.Combine(@"C:\Users\raymo", ".claude-work"));
    }

    [Fact]
    public void ResolveConfigJsonDirectory_WithDefaultDirProfile_ReturnsTheHomeRoot_WhereTheCliKeepsClaudeJson()
    {
        // Regression: the workspace-trust marker must land in ~/.claude.json (home root), not
        // ~/.claude/.claude.json, for a default-dir profile whose CLAUDE_CONFIG_DIR stays unset.
        var profile = new SessionProfile("default", new ClaudeConfig(Path.Combine(@"C:\Users\raymo", ".claude")));

        ClaudeConfigDirectory.ResolveConfigJsonDirectory(profile.Claude, @"C:\Users\raymo")
            .Should().Be(@"C:\Users\raymo");
    }

    [Fact]
    public void ResolveConfigJsonDirectory_WithoutProfile_ReturnsTheHomeRoot()
    {
        ClaudeConfigDirectory.ResolveConfigJsonDirectory(claude: null, @"C:\Users\raymo")
            .Should().Be(@"C:\Users\raymo");
    }
}
