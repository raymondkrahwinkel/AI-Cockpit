using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// <see cref="SessionProfileEntry"/>'s legacy fallback: an entry with no <see cref="SessionProfileEntry.Provider"/>
/// block is what every profile written before provider-neutral profiles (#26) looks like on disk — <c>ConfigDir</c>
/// sat at the top of the entry with nothing beside it. <see cref="SessionProfileEntry.ToDomain"/> must still read
/// that as a Claude profile pinned to that directory, or every operator's existing <c>cockpit.json</c> stops
/// resolving a login on the next start.
/// </summary>
public class SessionProfileEntryTests
{
    [Fact]
    public void ToDomain_WithNoProviderBlock_ReadsAsAClaudeProfilePinnedToTheTopLevelConfigDir()
    {
        var entry = new SessionProfileEntry
        {
            Label = "work",
            ConfigDir = "/home/raymond/.claude-work",
            ExecutablePath = null,
            Provider = null,
        };

        var profile = entry.ToDomain();

        // Fase 4: a provider-less (pre-#26) Claude entry is migrated to the bundled Claude provider plugin on load,
        // its top-level ConfigDir carried into the plugin's opaque config — so an operator's existing cockpit.json
        // keeps resolving the same login, now via the plugin. The equality against ClaudePluginProfile.Create is what
        // goes red if the migration is dropped or loses the ConfigDir.
        profile.Provider.Should().Be(SessionProvider.Plugin);
        profile.ProviderConfig.Should().Be(ClaudePluginProfile.Create("/home/raymond/.claude-work", null));
    }

    [Fact]
    public void FromDomain_AfterRoundTrippingALegacyEntry_KeepsTheConfigDirAndGainsAnExplicitProviderBlock()
    {
        var legacy = new SessionProfileEntry
        {
            Label = "work",
            ConfigDir = "/home/raymond/.claude-work",
            ExecutablePath = "/usr/local/bin/claude",
            Provider = null,
        };

        var resaved = SessionProfileEntry.FromDomain(legacy.ToDomain());

        // Fase 4: the legacy Claude entry was migrated to the plugin on load, so on re-save its settings move off the
        // top-level ConfigDir/ExecutablePath fields into the plugin's own config block — the one shape change, at the
        // point the shape actually changes. The directory and executable are preserved inside that block.
        resaved.ConfigDir.Should().BeEmpty();
        resaved.ExecutablePath.Should().BeNull();
        resaved.Provider.Should().NotBeNull();
        resaved.Provider!.Provider.Should().Be(SessionProvider.Plugin);
        resaved.Provider!.PluginProviderId.Should().Be(ClaudePluginProfile.ProviderId);
        resaved.Provider!.PluginConfigJson.Should().Contain("/home/raymond/.claude-work").And.Contain("/usr/local/bin/claude");
    }
}
