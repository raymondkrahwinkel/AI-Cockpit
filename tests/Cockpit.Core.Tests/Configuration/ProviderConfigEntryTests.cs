using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

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

        entry.Should().NotBeNull();
        entry!.Provider.Should().Be(SessionProvider.Plugin);
        entry.PluginProviderId.Should().Be("gemini-provider.gemini");
        entry.PluginConfigJson.Should().Be("""{"apiKey":"secret","model":"gemini-2.5-flash"}""");
        entry.BaseUrl.Should().BeNull();
        entry.Model.Should().BeNull();
        entry.ApiKey.Should().BeNull();
    }

    [Fact]
    public void ToDomain_WithAPluginProvider_RoundTripsBackToAPluginProviderConfig()
    {
        var original = new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret","model":"gemini-2.5-flash"}""");

        var roundTripped = ProviderConfigEntry.FromDomain(original)!.ToDomain(claudeConfigDir: string.Empty, claudeExecutablePath: null);

        roundTripped.Should().BeOfType<PluginProviderConfig>();
        var plugin = (PluginProviderConfig)roundTripped!;
        plugin.ProviderId.Should().Be(original.ProviderId);
        plugin.ConfigJson.Should().Be(original.ConfigJson);
    }
}
