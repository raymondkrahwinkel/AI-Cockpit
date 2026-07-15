using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeConfigPaths"/> (Fase 4) — the config-dir rules ported from the host: a default-dir (or absent)
/// config leaves CLAUDE_CONFIG_DIR unset (exporting it onto ~/.claude makes a logged-in CLI re-onboard), a
/// non-default profile dir is exported and is the dir whose .claude.json the CLI reads.
/// </summary>
public class ClaudeConfigPathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "cockpit-claude-home");

    [Fact]
    public void SpawnOverride_IsNull_ForADefaultOrAbsentConfigDir()
    {
        ClaudeConfigPaths.ResolveSpawnOverride(null, Home).Should().BeNull();
        ClaudeConfigPaths.ResolveSpawnOverride(Path.Combine(Home, ".claude"), Home).Should().BeNull();
    }

    [Fact]
    public void SpawnOverride_IsTheDirectory_ForANonDefaultConfigDir()
    {
        var dir = Path.Combine(Home, "work-profile");
        ClaudeConfigPaths.ResolveSpawnOverride(dir, Home).Should().Be(dir);
    }

    [Fact]
    public void ConfigJsonDirectory_IsHome_ForDefault_AndTheDir_ForNonDefault()
    {
        ClaudeConfigPaths.ResolveConfigJsonDirectory(null, Home).Should().Be(Home);

        var dir = Path.Combine(Home, "work-profile");
        ClaudeConfigPaths.ResolveConfigJsonDirectory(dir, Home).Should().Be(dir);
    }
}
