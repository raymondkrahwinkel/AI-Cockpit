using FluentAssertions;
using Cockpit.Core.Claude;
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
        var profile = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");

        var resolved = ClaudeConfigDirectory.Resolve(profile, @"C:\ignored", @"C:\Users\raymo");

        resolved.Should().Be(@"C:\Users\raymo\.claude-work");
    }

    [Fact]
    public void Resolve_WithoutProfile_AndEnvSet_ReturnsTheEnvDirectory()
    {
        var resolved = ClaudeConfigDirectory.Resolve(profile: null, @"C:\Users\raymo\.claude-alt", @"C:\Users\raymo");

        resolved.Should().Be(@"C:\Users\raymo\.claude-alt");
    }

    [Fact]
    public void Resolve_WithoutProfile_AndNoEnv_FallsBackToUserProfileDotClaude()
    {
        var resolved = ClaudeConfigDirectory.Resolve(profile: null, environmentConfigDir: null, @"C:\Users\raymo");

        resolved.Should().Be(Path.Combine(@"C:\Users\raymo", ".claude"));
    }

    [Fact]
    public void Resolve_WithoutProfile_AndBlankEnv_FallsBackToUserProfileDotClaude()
    {
        var resolved = ClaudeConfigDirectory.Resolve(profile: null, environmentConfigDir: "   ", @"C:\Users\raymo");

        resolved.Should().Be(Path.Combine(@"C:\Users\raymo", ".claude"));
    }
}
