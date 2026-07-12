using FluentAssertions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// Exercises login detection against a real temporary directory — existence-only, never
/// reads the file's contents (Iron Law #8).
/// </summary>
public class ClaudeProfileLoginCheckerTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeProfileLoginCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void IsLoggedIn_CredentialsFileExists_ReturnsTrue()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".credentials.json"), "{}");
        var checker = new ClaudeProfileLoginChecker();

        var result = checker.IsLoggedIn(new SessionProfile("default", _tempDir));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsLoggedIn_CredentialsFileAbsent_ReturnsFalse()
    {
        var checker = new ClaudeProfileLoginChecker();

        var result = checker.IsLoggedIn(new SessionProfile("default", _tempDir));

        result.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
