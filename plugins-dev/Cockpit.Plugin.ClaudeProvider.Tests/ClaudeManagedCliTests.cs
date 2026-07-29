using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// The Claude managed-CLI descriptor (AC-20): the platform-key mapping and the plan built from a release manifest.
/// The manifest fixture mirrors the real <c>downloads.claude.ai/.../manifest.json</c> shape (verified live), so these
/// assert the provider-specific knowledge without a network.
/// </summary>
public class ClaudeManagedCliTests
{
    private const string Manifest = """
        {
          "version": "2.1.212",
          "platforms": {
            "linux-x64":      { "binary": "claude",     "checksum": "aaaa1111", "size": 1 },
            "linux-x64-musl": { "binary": "claude",     "checksum": "bbbb2222", "size": 2 },
            "darwin-arm64":   { "binary": "claude",     "checksum": "cccc3333", "size": 3 },
            "win32-x64":      { "binary": "claude.exe", "checksum": "dddd4444", "size": 4 }
          }
        }
        """;

    [Theory]
    [InlineData("linux", "x64", false, "linux-x64")]
    [InlineData("linux", "x64", true, "linux-x64-musl")]
    [InlineData("darwin", "arm64", false, "darwin-arm64")]
    [InlineData("win32", "x64", false, "win32-x64")]
    public void PlatformKey_MapsOsArchAndMusl(string os, string arch, bool musl, string expected)
    {
        Assert.Equal(expected, ClaudeManagedCli.PlatformKey(new ManagedCliPlatform(os, arch, musl)));
    }

    [Fact]
    public void BuildPlan_Linux_UsesManifestBinaryAndChecksum_AsRawExecutable()
    {
        var plan = ClaudeManagedCli.BuildPlan("2.1.212", new ManagedCliPlatform("linux", "x64", false), Manifest);

        Assert.Equal("https://downloads.claude.ai/claude-code-releases/2.1.212/linux-x64/claude", plan.Url);
        Assert.Equal("aaaa1111", plan.ExpectedSha256);
        Assert.Equal("claude", plan.ExecutableFileName);
        Assert.Equal(ManagedCliArchiveFormat.RawBinary, plan.ArchiveFormat);
        Assert.True(plan.NeedsExecutableBit);
    }

    [Fact]
    public void BuildPlan_Musl_SelectsTheMuslVariant()
    {
        var plan = ClaudeManagedCli.BuildPlan("2.1.212", new ManagedCliPlatform("linux", "x64", true), Manifest);

        Assert.EndsWith("/linux-x64-musl/claude", plan.Url);
        Assert.Equal("bbbb2222", plan.ExpectedSha256);
    }

    [Fact]
    public void BuildPlan_Windows_NamesTheExeAndSkipsTheExecutableBit()
    {
        var plan = ClaudeManagedCli.BuildPlan("2.1.212", new ManagedCliPlatform("win32", "x64", false), Manifest);

        Assert.EndsWith("/win32-x64/claude.exe", plan.Url);
        Assert.Equal("claude.exe", plan.ExecutableFileName);
        Assert.False(plan.NeedsExecutableBit);
    }

    [Fact]
    public void BuildPlan_UnknownPlatform_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ClaudeManagedCli.BuildPlan("2.1.212", new ManagedCliPlatform("linux", "arm64", false), Manifest));

        Assert.Contains("linux-arm64", ex.Message);
    }
}
