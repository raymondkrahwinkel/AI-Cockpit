using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
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
    public void TranscriptDirectory_ForAMigratedNonDefaultProfile_ResolvesToItsOwnConfigDir_NotTheDefault()
    {
        // The bug: ClaudeConfigDirectory.Resolve got a null Claude (profile.Claude was null after migration) and fell
        // back to ~/.claude, so read-aloud tailed the wrong directory for a profile with its own config dir.
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null));

        var resolved = ClaudeConfigDirectory.Resolve(profile.Claude, environmentConfigDir: null, userProfileDirectory: "/home/raymond");

        resolved.Should().Be("/home/raymond/.claude-work");
    }

    [Fact]
    public void TranscriptDirectory_ForAConfiglessProfile_ResolvesToTheCliDefault()
    {
        var profile = new SessionProfile("default", ClaudePluginProfile.Create(configDir: null, executablePath: null));

        var resolved = ClaudeConfigDirectory.Resolve(profile.Claude, environmentConfigDir: null, userProfileDirectory: "/home/raymond");

        resolved.Should().Be(Path.Combine("/home/raymond", ".claude"));
    }
}
