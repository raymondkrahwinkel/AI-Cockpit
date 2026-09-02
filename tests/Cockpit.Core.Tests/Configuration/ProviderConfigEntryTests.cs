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
    public void APluginProviderConfig_LandsInItsOwnTwoFields_AndComesBackWhole()
    {
        var original = new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret","model":"gemini-2.5-flash"}""");

        var entry = ProviderConfigEntry.FromDomain(original);

        // On disk a plugin-backed profile is a provider id and one opaque blob — the typed Ollama/LM-Studio fields
        // stay empty, so nothing reads a plugin's settings as if they were a built-in provider's.
        Assert.NotNull(entry);
        Assert.Equal(SessionProvider.Plugin, entry!.Provider);
        Assert.Equal("gemini-provider.gemini", entry.PluginProviderId);
        Assert.Equal("""{"apiKey":"secret","model":"gemini-2.5-flash"}""", entry.PluginConfigJson);
        Assert.Null(entry.BaseUrl);
        Assert.Null(entry.Model);
        Assert.Null(entry.ApiKey);

        var roundTripped = entry.ToDomain(claudeConfigDir: string.Empty, claudeExecutablePath: null);

        var plugin = Assert.IsType<PluginProviderConfig>(roundTripped);
        Assert.Equal(original.ProviderId, plugin.ProviderId);
        Assert.Equal(original.ConfigJson, plugin.ConfigJson);
    }
}
