using System.Text.Json.Nodes;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Custom headers (AC-354) through <c>cockpit.json</c>: they survive the round-trip, and their values are covered by
/// the protection that keeps credentials out of a plain settings file and out of a backup.
/// </summary>
public class McpHeaderStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public McpHeaderStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task TheStoredHeaderValueIsActuallyReachedByTheSecretWalker()
    {
        var store = new McpServerStore(_configFilePath);
        await store.SaveAsync([new McpServerConfig
        {
            Name = "private-api",
            Transport = McpTransport.Http,
            Url = "https://api.example/mcp",
            ApiKey = "the-api-key",
            Headers = [new McpHeader("X-Api-Key", "the-header-value")],
        }]);

        var rewritten = SecretJsonWalker.Transform(
            JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath))!,
            SecretFields.ByName,
            (_, _) => "REDACTED");

        // The name rule alone proves nothing about whether the walker ever *reaches* this field: a header sits two
        // array levels deep (McpServers[i].Headers[j]), deeper than the ApiKey beside it. If the traversal stopped
        // short, every header value would sit in plain sight in cockpit.json and travel out in backups — with a test
        // on SecretFields.IsSecret still passing.
        Assert.Contains("McpServers[0].Headers[0].SecretValue", rewritten);
        Assert.Contains("McpServers[0].ApiKey", rewritten);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
