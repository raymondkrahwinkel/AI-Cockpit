using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

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
        Assert.Equal(SessionProvider.Plugin, profile.Provider);
        Assert.Equal(ClaudePluginProfile.Create("/home/raymond/.claude-work", null), profile.ProviderConfig);

        // Every field added since must read as "not set" rather than throwing or being invented — AC-139/AC-6's
        // DefaultKind in particular, which SessionKindDefaults then falls back to TTY for rather than picking SDK.
        Assert.Null(profile.EnvironmentVariables);
        Assert.Null(profile.EnabledMcpServerNames);
        Assert.Null(profile.DefaultWorkingDirectory);
        Assert.Null(profile.DefaultKind);
    }

    // A secret variable's value lands in the SecretValue field — the name the secret rule recognises — so it
    // rides the existing encrypt-at-rest/scrub-from-backups machinery; a plain value stays readable on purpose.
    [Fact]
    public void FromDomain_SplitsProfileEnvironmentVariablesBySecrecy_SoOnlySecretsRouteThroughEncryption()
    {
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null))
        {
            EnvironmentVariables =
            [
                new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS"),
                new ProfileEnvironmentVariable("MY_API_TOKEN", "s3cret", IsSecret: true),
            ],
        };

        var entry = SessionProfileEntry.FromDomain(profile);

        Assert.Equal(2, System.Linq.Enumerable.Count(entry.EnvironmentVariables!));
        Assert.Equal("/home/raymond/AI-OS", entry.EnvironmentVariables![0].Value);
        Assert.Null(entry.EnvironmentVariables[0].SecretValue);
        Assert.Null(entry.EnvironmentVariables[1].Value);
        Assert.Equal("s3cret", entry.EnvironmentVariables[1].SecretValue);
    }

    [Fact]
    public void ToDomain_MigratesALegacyClaudeProfilesTypedDefaults_IntoTheGenericOptionDefaults()
    {
        var entry = new SessionProfileEntry
        {
            Label = "work",
            ConfigDir = "/home/raymond/.claude-work",
            Provider = null,
            Defaults = new ProfileDefaultsEntry { PermissionMode = "bypassPermissions", Model = "opus", Effort = "high" },
        };

        var profile = entry.ToDomain();

        // Fase 4: a migrated Claude profile keeps its saved permission/model/effort as the generic OptionDefaults the
        // profile-edit and New-session dialogs read now, keyed by the plugin's own option keys — so the operator's
        // start settings survive the move to the plugin instead of silently resetting to the option defaults.
        Assert.NotNull(profile.Defaults!.OptionDefaults);
        Assert.Equal("bypassPermissions", profile.Defaults!.OptionDefaults!["permission-mode"]);
        Assert.Equal("opus", profile.Defaults!.OptionDefaults!["model"]);
        Assert.Equal("high", profile.Defaults!.OptionDefaults!["effort"]);
    }

}
