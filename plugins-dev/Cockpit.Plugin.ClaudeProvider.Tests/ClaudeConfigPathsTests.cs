
namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeConfigPaths` (Fase 4) — the config-dir rules ported from the host: a default-dir (or absent)
// config leaves CLAUDE_CONFIG_DIR unset (exporting it onto ~/.claude makes a logged-in CLI re-onboard), a
// non-default profile dir is exported and is the dir whose .claude.json the CLI reads.
public class ClaudeConfigPathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "cockpit-claude-home");

    [Fact]
    public void SpawnOverride_IsNull_ForADefaultOrAbsentConfigDir()
    {
        Assert.Null(ClaudeConfigPaths.ResolveSpawnOverride(null, Home));
        Assert.Null(ClaudeConfigPaths.ResolveSpawnOverride(Path.Combine(Home, ".claude"), Home));
    }

    [Fact]
    public void SpawnOverride_IsTheDirectory_ForANonDefaultConfigDir()
    {
        var dir = Path.Combine(Home, "work-profile");
        Assert.Equal(dir, ClaudeConfigPaths.ResolveSpawnOverride(dir, Home));
    }

    [Fact]
    public void ConfigJsonDirectory_IsHome_ForDefault_AndTheDir_ForNonDefault()
    {
        Assert.Equal(Home, ClaudeConfigPaths.ResolveConfigJsonDirectory(null, Home));

        var dir = Path.Combine(Home, "work-profile");
        Assert.Equal(dir, ClaudeConfigPaths.ResolveConfigJsonDirectory(dir, Home));
    }
}
