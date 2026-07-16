using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeExecutableLocator"/> (Fase 4) — resolving the <c>claude</c> command to a spawnable path so a bare
/// name finds the Windows <c>.cmd</c> npm shim that <see cref="System.Diagnostics.Process"/> would not. Only the
/// OS-independent contract is asserted here (a real PATH probe is environment-specific); the Windows shim probing
/// mirrors the proven Codex locator.
/// </summary>
public class ClaudeExecutableLocatorTests
{
    [Fact]
    public void Resolve_RootedPath_PassesThroughUnchanged()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "claude-does-not-need-lookup");

        ClaudeExecutableLocator.Resolve(rooted).Should().Be(rooted);
    }

    [Fact]
    public void Resolve_BareNameNotOnPath_ReturnsItUnchanged_SoStartStillGetsARealAttempt()
    {
        // A name PATH cannot resolve is returned as-is, so Process.Start makes a real attempt and yields a diagnosable
        // "file not found" rather than this resolver swallowing it.
        ClaudeExecutableLocator.Resolve("claude-provider-definitely-not-installed-xyz").Should().Be("claude-provider-definitely-not-installed-xyz");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankCommand_PassesThroughUnchanged(string command)
    {
        ClaudeExecutableLocator.Resolve(command).Should().Be(command);
    }

    [Fact]
    public void PickNewestClaudeExe_MissingInstallRoot_ReturnsNull()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"claude-code-{Guid.NewGuid():N}");

        ClaudeExecutableLocator.PickNewestClaudeExe(absent).Should().BeNull();
    }

    [Fact]
    public void PickNewestClaudeExe_PicksTheHighestVersionByVersionOrder_NotStringOrder()
    {
        // The desktop install keeps one directory per version; the newest one is what a fresh launch uses. Version
        // ordering must win over string ordering, so 2.1.209 beats 2.1.99 (which string-sorts higher).
        using var root = new _TempInstallRoot("2.1.99", "2.1.209", "2.1.205");

        var picked = ClaudeExecutableLocator.PickNewestClaudeExe(root.Path);

        picked.Should().Be(Path.Combine(root.Path, "2.1.209", "claude.exe"));
    }

    [Fact]
    public void PickNewestClaudeExe_IgnoresVersionDirectoriesWithoutTheExecutable()
    {
        using var root = new _TempInstallRoot("2.1.209");
        // A half-removed version directory with no claude.exe must not be chosen over a complete older one.
        Directory.CreateDirectory(Path.Combine(root.Path, "2.2.0"));

        var picked = ClaudeExecutableLocator.PickNewestClaudeExe(root.Path);

        picked.Should().Be(Path.Combine(root.Path, "2.1.209", "claude.exe"));
    }

    [Fact]
    public void PickNewestClaudeBinary_MissingVersionsDirectory_ReturnsNull()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"claude-versions-{Guid.NewGuid():N}");

        ClaudeExecutableLocator.PickNewestClaudeBinary(absent).Should().BeNull();
    }

    [Fact]
    public void PickNewestClaudeBinary_PicksTheHighestVersionByVersionOrder_NotStringOrder()
    {
        // The Linux/macOS installer keeps the binaries as files named by version, directly under the versions
        // directory (~/.local/share/claude/versions). The newest is what a launcher symlink points at; version
        // ordering must win, so 2.1.209 beats 2.1.99 (which string-sorts higher).
        using var versions = new _TempVersionsDir("2.1.99", "2.1.209", "2.1.205");

        var picked = ClaudeExecutableLocator.PickNewestClaudeBinary(versions.Path);

        picked.Should().Be(Path.Combine(versions.Path, "2.1.209"));
    }

    [Fact]
    public void PickNewestClaudeBinary_IgnoresFilesWhoseNameIsNotAVersion()
    {
        using var versions = new _TempVersionsDir("2.1.209");
        File.WriteAllText(Path.Combine(versions.Path, "latest"), string.Empty); // a non-version file must not win

        var picked = ClaudeExecutableLocator.PickNewestClaudeBinary(versions.Path);

        picked.Should().Be(Path.Combine(versions.Path, "2.1.209"));
    }

    private sealed class _TempVersionsDir : IDisposable
    {
        public string Path { get; }

        public _TempVersionsDir(params string[] versionFiles)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claude-versions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            foreach (var version in versionFiles)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, version), string.Empty);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class _TempInstallRoot : IDisposable
    {
        public string Path { get; }

        public _TempInstallRoot(params string[] versionsWithExecutable)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claude-code-{Guid.NewGuid():N}");
            foreach (var version in versionsWithExecutable)
            {
                var directory = System.IO.Path.Combine(Path, version);
                Directory.CreateDirectory(directory);
                File.WriteAllText(System.IO.Path.Combine(directory, "claude.exe"), string.Empty);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory; a locked file must not fail the test.
            }
        }
    }
}
