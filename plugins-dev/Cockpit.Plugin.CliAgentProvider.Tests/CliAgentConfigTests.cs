using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// A plain `record`'s auto-generated `ToString()` would print `CliAgentConfig.ApiKey` in the
// clear — a leak surface anywhere this config lands in a log line or exception message.
public class CliAgentConfigTests
{
    [Fact]
    public async Task Credentials_ReportOnlyTheirSource_AndOverrideCachedSignOutAsync()
    {
        var secret = Guid.NewGuid().ToString();
        var variable = "AC2069_" + Guid.NewGuid().ToString("N");
        var config = new CliAgentConfig(Command: @"C:\Program Files\Codex\codex.exe", Model: "gpt-5-codex", WorkingDirectory: @"C:\work", AuthEnvVar: variable, ApiKey: secret,
            ConfigDir: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var text = config.ToString();

        Assert.False(text.Contains(secret, StringComparison.Ordinal));
        Assert.Contains("***", text);
        Assert.Contains("gpt-5-codex", text);
        Assert.Contains(@"C:\work", text);

        var withoutKey = JsonSerializer.Serialize(config with { ApiKey = null });
        await CodexLoginStatus.RefreshAsync(withoutKey, DateTimeOffset.UtcNow, null,
            (_, _, _) => Task.FromResult<CodexLoginStatus.LoginStatus?>(new(false, PluginCredentialKind.Unknown)), CancellationToken.None);
        Assert.False(CodexLoginStatus.IsLoggedIn(withoutKey));
        Assert.Equal(PluginCredentialKind.Unknown, CodexLoginStatus.CredentialKind(withoutKey));
        try
        {
            Environment.SetEnvironmentVariable(variable, secret);
            Assert.True(CodexLoginStatus.IsLoggedIn(withoutKey));
            Assert.Equal(PluginCredentialKind.ApiKeyFromSecret, CodexLoginStatus.CredentialKind(withoutKey));
            Assert.Equal(PluginCredentialKind.ApiKey, CodexLoginStatus.CredentialKind(JsonSerializer.Serialize(config)));
            Assert.False((config with { ApiKey = null }).ToString().Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
