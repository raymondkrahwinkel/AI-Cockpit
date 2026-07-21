using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeWorkspaceTrust"/> — marking a working directory trusted in the shared <c>~/.claude.json</c>.
/// The invariants that matter are the ones that broke MCP for a session started after a TTY: the write must be
/// atomic (no zero-length middle state a concurrent claude could read and reset from), it must leave no temp files
/// behind, it must preserve every other key/project, and it must not rewrite the file at all once the directory is
/// already trusted (each needless rewrite races the live TTY claude writing the same file).
/// </summary>
public class ClaudeWorkspaceTrustTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "cockpit-trust-" + Guid.NewGuid().ToString("N"));

    private string ClaudeJson => Path.Combine(_configDir, ".claude.json");

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Fact]
    public void MarksTheDirectoryTrusted_CreatingTheFile_WhenAbsent()
    {
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        var root = JsonNode.Parse(File.ReadAllText(ClaudeJson))!.AsObject();
        root["projects"]![@"D:\Projects\Cockpit"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void PreservesEveryOtherKeyAndProject()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ClaudeJson, """
        {
          "numStartups": 42,
          "oauthAccount": { "emailAddress": "keep@me.test" },
          "projects": { "D:\\Other": { "hasTrustDialogAccepted": true, "history": ["a"] } }
        }
        """);

        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        var root = JsonNode.Parse(File.ReadAllText(ClaudeJson))!.AsObject();
        root["numStartups"]!.GetValue<int>().Should().Be(42);
        root["oauthAccount"]!["emailAddress"]!.GetValue<string>().Should().Be("keep@me.test");
        root["projects"]![@"D:\Other"]!["history"]!.AsArray().Should().HaveCount(1);
        root["projects"]![@"D:\Projects\Cockpit"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void DoesNotRewriteTheFile_WhenTheDirectoryIsAlreadyTrusted()
    {
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");
        var firstWrite = File.GetLastWriteTimeUtc(ClaudeJson);
        var firstBytes = File.ReadAllBytes(ClaudeJson);

        // A second, third call for the same already-trusted directory must be a no-op on disk — every rewrite of the
        // shared file races a live TTY claude, and skipping it is what keeps that race from ever stripping the session.
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        File.GetLastWriteTimeUtc(ClaudeJson).Should().Be(firstWrite, "an already-trusted directory must not trigger a rewrite");
        File.ReadAllBytes(ClaudeJson).Should().Equal(firstBytes);
    }

    [Fact]
    public void LeavesNoTempFilesBehind()
    {
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        Directory.EnumerateFiles(_configDir)
            .Select(Path.GetFileName)
            .Should().ContainSingle().Which.Should().Be(".claude.json");
    }

    [Fact]
    public void WritesWellFormedJson_ThatRoundTrips()
    {
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\A");
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\B");

        // No JsonException means every write landed as a complete document — the property the truncate-in-place path
        // could not guarantee under a concurrent reader.
        var act = () => JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(ClaudeJson));
        act.Should().NotThrow();
    }

    [Fact]
    public void FlipsAnExplicitFalse_ToTrue()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ClaudeJson, """{ "projects": { "D:\\Projects\\Cockpit": { "hasTrustDialogAccepted": false } } }""");

        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        var root = JsonNode.Parse(File.ReadAllText(ClaudeJson))!.AsObject();
        root["projects"]![@"D:\Projects\Cockpit"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void OverwritesANonBoolTrustValue_WithBoolTrue()
    {
        // A malformed value (a string, a number) is not "already trusted" — the CLI reads a real boolean, so the mark
        // must normalise it rather than leave the directory effectively untrusted.
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ClaudeJson, """{ "projects": { "D:\\X": { "hasTrustDialogAccepted": "true" } } }""");

        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\X");

        var root = JsonNode.Parse(File.ReadAllText(ClaudeJson))!.AsObject();
        root["projects"]![@"D:\X"]!["hasTrustDialogAccepted"]!.GetValueKind().Should().Be(JsonValueKind.True);
    }

    [Fact]
    public void Throws_AndLeavesTheFileIntact_WhenAnExistingFileIsNotAnObject()
    {
        // A torn/corrupt existing file (here a bare array) must never be silently replaced with an empty root — that
        // is precisely the reset-to-defaults data loss this type guards against. It throws, and the file is untouched.
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ClaudeJson, "[1, 2, 3]");

        var act = () => ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(_configDir, @"D:\Projects\Cockpit");

        act.Should().Throw<IOException>();
        File.ReadAllText(ClaudeJson).Should().Be("[1, 2, 3]", "a file that could not be read must not be overwritten");
    }
}
