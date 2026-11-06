
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// `OpencodeConfig`'s `ToString()` override (AC-783, mirroring Cockpit.Plugin.KimiProvider.Tests'
// KimiConfigTests-equivalent coverage): a plain `record`'s auto-generated `ToString()` would print
// `ApiKey` in the clear — a leak surface anywhere this config lands in a log line or exception message.
public class OpencodeConfigTests
{
    [Fact]
    public void Credentials_ReportOnlyTheirSource_WithoutExposingKeys()
    {
        var secret = Guid.NewGuid().ToString();
        var variable = "AC2069_" + Guid.NewGuid().ToString("N");
        var config = new OpencodeConfig(AuthEnvVar: variable, ApiKey: secret);

        var text = config.ToString();

        Assert.False(text.Contains(secret, StringComparison.Ordinal));
        Assert.Contains("***", text);

        var withoutKey = JsonSerializer.Serialize(config with { ApiKey = null });
        Assert.Equal(PluginCredentialKind.Unknown, OpencodeConfig.CredentialKindOf(withoutKey));
        try
        {
            Environment.SetEnvironmentVariable(variable, secret);
            Assert.Equal(PluginCredentialKind.ApiKeyFromSecret, OpencodeConfig.CredentialKindOf(withoutKey));
            Assert.Equal(PluginCredentialKind.ApiKey, OpencodeConfig.CredentialKindOf(JsonSerializer.Serialize(config)));
            Assert.False((config with { ApiKey = null }).ToString().Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void BuildEnvironmentVariables_WithAnApiKey_SetsItUnderAuthEnvVar()
    {
        var config = new OpencodeConfig(AuthEnvVar: "OPENCODE_API_KEY", ApiKey: "sk-value");

        var env = config.BuildEnvironmentVariables();

        Assert.Equal("sk-value", env["OPENCODE_API_KEY"]);
    }
}
