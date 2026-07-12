using FluentAssertions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// Exercises the auto-detect logic against a fake directory-existence predicate — no real
/// filesystem access, so this covers arbitrary "home" layouts without touching disk.
/// </summary>
/// <remarks>
/// Paths are built with <see cref="Path.Combine(string, string)"/> rather than hard-coded with a
/// backslash: the label derivation relies on <see cref="Path.GetFileName(string)"/>, which only
/// splits on the running OS's separator, so a literal <c>C:\...</c> string would parse as one
/// segment on Linux and break the test off Windows.
/// </remarks>
public class ClaudeCliProfileDetectorTests
{
    private static readonly string Home = Path.Combine("fake-home");
    private static readonly string DefaultDir = Path.Combine(Home, ".claude");
    private static readonly string PersonalDir = Path.Combine(Home, ".claude-personal");
    private static readonly string WorkDir = Path.Combine(Home, ".claude-work");

    [Fact]
    public void Detect_AllCandidatesExist_ReturnsProfileForEachWithDerivedLabels()
    {
        var candidates = new[] { DefaultDir, PersonalDir, WorkDir };

        var profiles = ClaudeCliProfileDetector.Detect(candidates, _ => true);

        profiles.Should().HaveCount(3);
        profiles.Should().Contain(p => p.Label == "default" && p.ConfigDir == DefaultDir);
        profiles.Should().Contain(p => p.Label == "personal" && p.ConfigDir == PersonalDir);
        profiles.Should().Contain(p => p.Label == "work" && p.ConfigDir == WorkDir);
    }

    [Fact]
    public void Detect_OnlySomeCandidatesExist_SkipsMissingOnes()
    {
        var candidates = new[] { DefaultDir, PersonalDir, WorkDir };

        var profiles = ClaudeCliProfileDetector.Detect(candidates, dir => dir.EndsWith(".claude"));

        profiles.Should().ContainSingle().Which.ConfigDir.Should().Be(DefaultDir);
    }

    [Fact]
    public void Detect_NoCandidatesExist_ReturnsEmpty()
    {
        var candidates = new[] { DefaultDir };

        var profiles = ClaudeCliProfileDetector.Detect(candidates, _ => false);

        profiles.Should().BeEmpty();
    }
}
