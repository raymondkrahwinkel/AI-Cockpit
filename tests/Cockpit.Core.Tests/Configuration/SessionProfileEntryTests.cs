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

        // Asserting only Provider == ClaudeCli would still be green if ToDomain fell back to a Claude
        // profile with an *empty* ConfigDir instead of the one the entry actually carried — the ConfigDir
        // assertion is what goes red if the `?? new ClaudeConfig(ConfigDir, ExecutablePath)` fallback in
        // SessionProfileEntry.ToDomain is ever dropped or replaced with a default.
        profile.Provider.Should().Be(SessionProvider.ClaudeCli);
        profile.Claude.Should().NotBeNull();
        profile.Claude!.ConfigDir.Should().Be("/home/raymond/.claude-work");
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

        resaved.ConfigDir.Should().Be("/home/raymond/.claude-work");
        resaved.ExecutablePath.Should().Be("/usr/local/bin/claude");
        // A profile saved by this version says which provider it runs under explicitly (#26) — even Claude's,
        // which an older cockpit left implicit by writing no Provider block at all.
        resaved.Provider.Should().NotBeNull();
        resaved.Provider!.Provider.Should().Be(SessionProvider.ClaudeCli);
    }
}
