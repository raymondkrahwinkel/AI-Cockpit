using FluentAssertions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

public class BundledClaudeExecutableSelectorTests
{
    private const string ClaudeCodeRoot = @"C:\fake-appdata\Claude\claude-code";

    [Fact]
    public void SelectNewestExecutable_MultipleVersions_PicksHighest()
    {
        var versions = new[] { "1.0.0", "2.1.197", "2.1.5" };

        var result = BundledClaudeExecutableSelector.SelectNewestExecutable(
            ClaudeCodeRoot, versions, "claude.exe", _ => true);

        result.Should().Be(Path.Combine(ClaudeCodeRoot, "2.1.197", "claude.exe"));
    }

    [Fact]
    public void SelectNewestExecutable_NewestVersionMissingExecutable_ReturnsNull()
    {
        // Only the single newest version folder is considered — no fallback to older
        // folders. A partially-installed newest version is surfaced as "not found" rather
        // than silently spawning an older, possibly-stale executable.
        var versions = new[] { "1.0.0", "2.1.197" };
        var executablePresent = new HashSet<string>
        {
            Path.Combine(ClaudeCodeRoot, "1.0.0", "claude.exe"),
        };

        var result = BundledClaudeExecutableSelector.SelectNewestExecutable(
            ClaudeCodeRoot, versions, "claude.exe", executablePresent.Contains);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectNewestExecutable_NonVersionFolderNames_AreIgnored()
    {
        var versions = new[] { "not-a-version", "2.1.197" };

        var result = BundledClaudeExecutableSelector.SelectNewestExecutable(
            ClaudeCodeRoot, versions, "claude.exe", _ => true);

        result.Should().Be(Path.Combine(ClaudeCodeRoot, "2.1.197", "claude.exe"));
    }

    [Fact]
    public void SelectNewestExecutable_NoVersionFolders_ReturnsNull()
    {
        var result = BundledClaudeExecutableSelector.SelectNewestExecutable(
            ClaudeCodeRoot, [], "claude.exe", _ => true);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectNewestExecutable_ExecutableMissingInAllFolders_ReturnsNull()
    {
        var versions = new[] { "2.1.197" };

        var result = BundledClaudeExecutableSelector.SelectNewestExecutable(
            ClaudeCodeRoot, versions, "claude.exe", _ => false);

        result.Should().BeNull();
    }
}
