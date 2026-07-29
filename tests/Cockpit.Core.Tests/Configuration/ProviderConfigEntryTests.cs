using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// <see cref="ProviderConfigEntry"/> round-tripping a <see cref="PluginProviderConfig"/> (#45) — the
/// generic on-disk shape a plugin-backed profile uses, alongside the existing Ollama/LM-Studio cases.
/// </summary>
public class ProviderConfigEntryTests
{
    [Fact]
    public void FromDomain_WithAPluginProviderConfig_MapsProviderIdAndConfigJson()
    {
        var config = new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret","model":"gemini-2.5-flash"}""");

        var entry = ProviderConfigEntry.FromDomain(config);

        Assert.NotNull(entry);
        Assert.Equal(SessionProvider.Plugin, entry!.Provider);
        Assert.Equal("gemini-provider.gemini", entry.PluginProviderId);
        Assert.Equal("""{"apiKey":"secret","model":"gemini-2.5-flash"}""", entry.PluginConfigJson);
        Assert.Null(entry.BaseUrl);
        Assert.Null(entry.Model);
        Assert.Null(entry.ApiKey);
    }

    [Fact]
    public void ToDomain_WithAPluginProvider_RoundTripsBackToAPluginProviderConfig()
    {
        var original = new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret","model":"gemini-2.5-flash"}""");

        var roundTripped = ProviderConfigEntry.FromDomain(original)!.ToDomain(claudeConfigDir: string.Empty, claudeExecutablePath: null);

        Assert.IsType<PluginProviderConfig>(roundTripped);
        var plugin = (PluginProviderConfig)roundTripped!;
        Assert.Equal(original.ProviderId, plugin.ProviderId);
        Assert.Equal(original.ConfigJson, plugin.ConfigJson);
    }
}
