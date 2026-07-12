using System.Text.Json.Nodes;
using FluentAssertions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Profiles;

public class WorkspaceTrustWriterTests : IDisposable
{
    private readonly string _configDir;

    public WorkspaceTrustWriterTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    [Fact]
    public void MarkWorkingDirectoryTrusted_NoExistingFile_CreatesItWithTrustSet()
    {
        var writer = new WorkspaceTrustWriter();
        var claudeJsonPath = Path.Combine(_configDir, ".claude.json");

        writer.MarkWorkingDirectoryTrusted(_configDir, @"C:\repo\zyra-voice");

        var root = ReadJson(claudeJsonPath);
        root["projects"]![@"C:\repo\zyra-voice"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void MarkWorkingDirectoryTrusted_ExistingUnrelatedKeys_PreservesThem()
    {
        var claudeJsonPath = Path.Combine(_configDir, ".claude.json");
        File.WriteAllText(claudeJsonPath, """
            {
              "userId": "some-user-id",
              "numStartups": 42,
              "projects": {
                "C:\\other\\repo": { "hasTrustDialogAccepted": true, "someOtherFlag": "keep-me" }
              }
            }
            """);

        var writer = new WorkspaceTrustWriter();
        writer.MarkWorkingDirectoryTrusted(_configDir, @"C:\repo\zyra-voice");

        var root = ReadJson(claudeJsonPath);
        root["userId"]!.GetValue<string>().Should().Be("some-user-id");
        root["numStartups"]!.GetValue<int>().Should().Be(42);
        root["projects"]![@"C:\other\repo"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
        root["projects"]![@"C:\other\repo"]!["someOtherFlag"]!.GetValue<string>().Should().Be("keep-me");
        root["projects"]![@"C:\repo\zyra-voice"]!["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void MarkWorkingDirectoryTrusted_ExistingProjectEntryWithOtherKeys_PreservesThem()
    {
        var claudeJsonPath = Path.Combine(_configDir, ".claude.json");
        File.WriteAllText(claudeJsonPath, """
            {
              "projects": {
                "C:\\repo\\zyra-voice": { "hasTrustDialogAccepted": false, "allowedTools": ["Read"] }
              }
            }
            """);

        var writer = new WorkspaceTrustWriter();
        writer.MarkWorkingDirectoryTrusted(_configDir, @"C:\repo\zyra-voice");

        var root = ReadJson(claudeJsonPath);
        var entry = root["projects"]![@"C:\repo\zyra-voice"]!;
        entry["hasTrustDialogAccepted"]!.GetValue<bool>().Should().BeTrue();
        entry["allowedTools"]!.AsArray().Should().ContainSingle(t => t!.GetValue<string>() == "Read");
    }

    [Fact]
    public void MarkWorkingDirectoryTrusted_CalledTwice_IsIdempotent()
    {
        var claudeJsonPath = Path.Combine(_configDir, ".claude.json");
        var writer = new WorkspaceTrustWriter();

        writer.MarkWorkingDirectoryTrusted(_configDir, @"C:\repo\zyra-voice");
        var firstRun = File.ReadAllText(claudeJsonPath);
        writer.MarkWorkingDirectoryTrusted(_configDir, @"C:\repo\zyra-voice");
        var secondRun = File.ReadAllText(claudeJsonPath);

        secondRun.Should().Be(firstRun);
    }

    private static JsonObject ReadJson(string path)
    {
        using var stream = File.OpenRead(path);
        var node = JsonNode.Parse(stream);
        return (JsonObject)node!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }
}
