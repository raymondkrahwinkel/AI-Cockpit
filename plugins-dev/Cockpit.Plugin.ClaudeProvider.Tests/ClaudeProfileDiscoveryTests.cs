using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// The provider-specific behaviours the host used to hold in-tree, now owned by the plugin (weg A / Fase 4):
/// self-detection of well-known config directories and the existence-only login gate. Detection is exercised
/// against a fake directory-existence predicate; the login gate against a real temp directory.
/// </summary>
public class ClaudeProfileDiscoveryTests
{
    private static readonly string RealHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Detect_ExistingCandidates_MintProfilesWithDerivedLabels_AndDefaultDirStaysBlank()
    {
        // The real ~/.claude is the CLI default → its ConfigDir stays blank so the profile follows the CLI wherever
        // it puts the default dir; a named directory is pinned. A fake ".claude-work" under an arbitrary home
        // exercises the pinned branch without depending on the machine actually having that directory.
        var defaultDir = Path.Combine(RealHome, ".claude");
        var workDir = Path.Combine("fake-home", ".claude-work");

        var profiles = ClaudeProfileDiscovery.Detect([defaultDir, workDir], _ => true);

        profiles.Should().HaveCount(2);

        var @default = profiles.Single(p => p.Label == "default");
        ClaudeProviderConfig.Parse(@default.ConfigJson).ConfigDir.Should().BeNull();

        var work = profiles.Single(p => p.Label == "work");
        ClaudeProviderConfig.Parse(work.ConfigJson).ConfigDir.Should().Be(workDir);
    }

    [Fact]
    public void Detect_SkipsMissingCandidates()
    {
        var profiles = ClaudeProfileDiscovery.Detect(
            [Path.Combine("fake-home", ".claude"), Path.Combine("fake-home", ".claude-work")],
            dir => dir.EndsWith(".claude-work"));

        profiles.Should().ContainSingle().Which.Label.Should().Be("work");
    }

    [Fact]
    public void Detect_NoCandidatesExist_ReturnsEmpty()
    {
        ClaudeProfileDiscovery.Detect([Path.Combine("fake-home", ".claude")], _ => false).Should().BeEmpty();
    }

    [Fact]
    public void IsLoggedIn_TrueOnlyWhenCredentialsFileExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var configJson = System.Text.Json.JsonSerializer.Serialize(
                new ClaudeProviderConfig(ConfigDir: dir), ClaudeProviderConfig.JsonOptions);

            ClaudeProfileDiscovery.IsLoggedIn(configJson).Should().BeFalse("no credentials file yet");

            File.WriteAllText(Path.Combine(dir, ".credentials.json"), "{}");
            ClaudeProfileDiscovery.IsLoggedIn(configJson).Should().BeTrue("the credentials file now exists");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
