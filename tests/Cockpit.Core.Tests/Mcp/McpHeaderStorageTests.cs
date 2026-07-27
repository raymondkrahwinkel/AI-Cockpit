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
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheHeaders()
    {
        var store = new McpServerStore(_configFilePath);
        var server = new McpServerConfig
        {
            Name = "private-api",
            Transport = McpTransport.Http,
            Url = "https://api.example/mcp",
            Headers = [new McpHeader("X-Api-Key", "the-key"), new McpHeader("X-Tenant", "acme")],
        };

        await store.SaveAsync([server]);
        var loaded = await store.LoadAsync();

        var reloaded = Assert.Single(loaded);
        Assert.Collection(
            reloaded.Headers,
            first =>
            {
                Assert.Equal("X-Api-Key", first.Name);
                Assert.Equal("the-key", first.Value);
            },
            second =>
            {
                Assert.Equal("X-Tenant", second.Name);
                Assert.Equal("acme", second.Value);
            });
    }

    [Fact]
    public async Task LoadAsync_DropsAHalfWrittenRowFromAHandEditedConfig()
    {
        var store = new McpServerStore(_configFilePath);
        await store.SaveAsync([new McpServerConfig
        {
            Name = "private-api",
            Transport = McpTransport.Http,
            Url = "https://api.example/mcp",
            Headers = [new McpHeader("X-Api-Key", "the-key"), new McpHeader(string.Empty, "orphan")],
        }]);

        Assert.Single(Assert.Single(await store.LoadAsync()).Headers);
    }

    [Fact]
    public void TheStoredHeaderValueIsCoveredByTheSecretRule_AndTheNameIsNot()
    {
        // This is why the on-disk field is called SecretValue rather than Value: encryption and backup-scrubbing both
        // decide by the name of the JSON field, and a field called "Value" is the gap free-form rows fell into once
        // before (AC-295) — a pasted token left readable and out of the scrubber's reach.
        Assert.True(SecretFields.ByName.IsSecret("SecretValue"));
        Assert.False(SecretFields.ByName.IsSecret("Value"));
        Assert.False(SecretFields.ByName.IsSecret("Name"));
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
