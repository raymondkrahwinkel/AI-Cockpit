using FluentAssertions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// Exercises the auto-detect logic against a fake directory-existence predicate — no real
/// filesystem access, so this covers arbitrary "home" layouts without touching disk.
/// </summary>
public class ClaudeProfileAutoDetectorTests
{
    [Fact]
    public void Detect_AllCandidatesExist_ReturnsProfileForEachWithDerivedLabels()
    {
        var candidates = new[]
        {
            @"C:\fake-home\.claude",
            @"C:\fake-home\.claude-personal",
            @"C:\fake-home\.claude-work",
        };

        var profiles = ClaudeProfileAutoDetector.Detect(candidates, _ => true);

        profiles.Should().HaveCount(3);
        profiles.Should().Contain(p => p.Label == "default" && p.ConfigDir == @"C:\fake-home\.claude");
        profiles.Should().Contain(p => p.Label == "personal" && p.ConfigDir == @"C:\fake-home\.claude-personal");
        profiles.Should().Contain(p => p.Label == "work" && p.ConfigDir == @"C:\fake-home\.claude-work");
    }

    [Fact]
    public void Detect_OnlySomeCandidatesExist_SkipsMissingOnes()
    {
        var candidates = new[]
        {
            @"C:\fake-home\.claude",
            @"C:\fake-home\.claude-personal",
            @"C:\fake-home\.claude-work",
        };

        var profiles = ClaudeProfileAutoDetector.Detect(candidates, dir => dir.EndsWith(".claude"));

        profiles.Should().ContainSingle().Which.ConfigDir.Should().Be(@"C:\fake-home\.claude");
    }

    [Fact]
    public void Detect_NoCandidatesExist_ReturnsEmpty()
    {
        var candidates = new[] { @"C:\fake-home\.claude" };

        var profiles = ClaudeProfileAutoDetector.Detect(candidates, _ => false);

        profiles.Should().BeEmpty();
    }
}
