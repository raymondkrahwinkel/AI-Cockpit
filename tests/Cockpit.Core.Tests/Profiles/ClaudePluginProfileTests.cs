using Cockpit.Core.Profiles;
using FluentAssertions;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// <see cref="ClaudePluginProfile"/> and <see cref="SessionProfile.Claude"/>: after Fase 4 a Claude profile carries
/// the bundled plugin's config, but host-side Claude features (the read-aloud/status transcript directory, the login
/// check) still ask a profile for its <see cref="SessionProfile.Claude"/>. Regression cover for the adversarial-review
/// finding that these saw <see langword="null"/> for a migrated profile and tailed the wrong directory.
/// </summary>
public class ClaudePluginProfileTests
{
    [Fact]
    public void Claude_ForAMigratedPluginProfile_ReconstructsTheConfigFromThePluginConfigJson()
    {
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", "/usr/local/bin/claude"));

        profile.Claude.Should().NotBeNull();
        profile.Claude!.ConfigDir.Should().Be("/home/raymond/.claude-work");
        profile.Claude!.ExecutablePath.Should().Be("/usr/local/bin/claude");
    }

    [Fact]
    public void Claude_ForANonClaudeProfile_IsNull()
    {
        var profile = new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

        profile.Claude.Should().BeNull();
    }

    [Fact]
    public void MigratedNonDefaultProfile_ReconstructsItsOwnConfigDir()
    {
        // The bug this guards: after migration profile.Claude read as null, so consumers fell back to ~/.claude and
        // read-aloud/login/spawn all used the wrong directory for a profile with its own config dir. The provider
        // plugin resolves the transcript/credentials directory from this reconstructed ConfigDir.
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null));

        profile.Claude!.ConfigDir.Should().Be("/home/raymond/.claude-work");
    }

    [Fact]
    public void MigratedConfiglessProfile_ReconstructsABlankConfigDir_SoItFollowsTheCliDefault()
    {
        // A blank ConfigDir is the signal the provider plugin resolves to the CLI default (~/.claude); reconstructing
        // it as blank rather than null keeps a default profile a real, resolvable Claude profile after migration.
        var profile = new SessionProfile("default", ClaudePluginProfile.Create(configDir: null, executablePath: null));

        profile.Claude.Should().NotBeNull();
        profile.Claude!.ConfigDir.Should().BeNullOrEmpty();
    }
}
