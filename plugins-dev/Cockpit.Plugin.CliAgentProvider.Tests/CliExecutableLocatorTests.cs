using Cockpit.Plugin.CliAgentProvider;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CliExecutableLocator"/> (#45 fase B1). B2 caveat: the "found via PATH" branch is only
/// exercised here against a temp directory this test controls, not Raymond's real npm-global install — see
/// the class's own remarks for why cross-platform npm-shim discovery still needs live verification.
/// </summary>
public class CliExecutableLocatorTests
{
    [Fact]
    public void Resolve_AnAbsolutePath_IsReturnedUnchanged()
    {
        var absolutePath = Path.Combine(Path.GetTempPath(), "codex.exe");

        CliExecutableLocator.Resolve(absolutePath).Should().Be(absolutePath);
    }

    [Fact]
    public void Resolve_ABareCommandNotFoundOnPath_IsReturnedUnchanged_SoProcessStartStillGetsARealAttempt()
    {
        CliExecutableLocator.Resolve("definitely-not-a-real-cli-tool-12345").Should().Be("definitely-not-a-real-cli-tool-12345");
    }

    [Fact]
    public void Resolve_ABareCommandFoundDirectlyOnPath_ReturnsTheFullPath()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var executable = Path.Combine(directory.FullName, "fake-codex-tool");
            File.WriteAllText(executable, "not a real binary, just needs to exist");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", $"{directory.FullName}{Path.PathSeparator}{originalPath}");
            try
            {
                CliExecutableLocator.Resolve("fake-codex-tool").Should().Be(executable);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
