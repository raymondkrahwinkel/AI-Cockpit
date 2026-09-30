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
        Assert.Empty(resaved.ConfigDir);
        Assert.Null(resaved.ExecutablePath);
        Assert.NotNull(resaved.Provider);
        Assert.Equal(SessionProvider.Plugin, resaved.Provider!.Provider);
        Assert.Equal(ClaudePluginProfile.ProviderId, resaved.Provider!.PluginProviderId);
        Assert.Contains("/home/raymond/.claude-work", resaved.Provider!.PluginConfigJson);
        Assert.Contains("/usr/local/bin/claude", resaved.Provider!.PluginConfigJson);
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
    public void ToDomain_AfterRoundTripping_KeepsEachVariablesValueAndSecrecy()
    {
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null))
        {
            EnvironmentVariables =
            [
                new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS"),
                new ProfileEnvironmentVariable("MY_API_TOKEN", "s3cret", IsSecret: true),
            ],
        };

        var roundTripped = SessionProfileEntry.FromDomain(profile).ToDomain();

        Assert.Equal(profile.EnvironmentVariables, roundTripped.EnvironmentVariables);
    }

    [Fact]
    public void RoundTrip_KeepsTheMcpPreSelectionAndDefaultWorkingDirectory()
    {
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null))
        {
            EnabledMcpServerNames = ["youtrack", "docker"],
            DefaultWorkingDirectory = "/home/raymond/RiderProjects/App",
        };

        var roundTripped = SessionProfileEntry.FromDomain(profile).ToDomain();

        Assert.Equal(new[] { "youtrack", "docker" }, roundTripped.EnabledMcpServerNames);
        Assert.Equal("/home/raymond/RiderProjects/App", roundTripped.DefaultWorkingDirectory);
    }

    // What every profile written before these fields existed looks like on disk: a label and ConfigDir, nothing
    // else. Each absent field must read as "not set" rather than throwing or being invented — AC-139/AC-6's
    // DefaultKind in particular, which SessionKindDefaults then falls back to TTY for rather than picking SDK.
    [Fact]
    public void ToDomain_WithoutTheNewerFields_LeavesThemAllUnset_SoOlderConfigsKeepWorking()
    {
        var entry = new SessionProfileEntry { Label = "work", ConfigDir = "/home/raymond/.claude-work" };

        var profile = entry.ToDomain();

        Assert.Null(profile.EnvironmentVariables);
        Assert.Null(profile.EnabledMcpServerNames);
        Assert.Null(profile.DefaultWorkingDirectory);
        Assert.Null(profile.DefaultKind);
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

    [Fact]
    public void ToDomain_WhenOptionDefaultsWereSeededWithPluginDefaults_RecoversThemFromTheAuthoritativeTypedFields()
    {
        // Root-cause regression: an intermediate build seeded OptionDefaults with the plugin's own defaults
        // (permission-mode=default, effort=medium, no model) instead of the operator's saved values, shadowing the
        // still-correct typed fields. The typed fields are authoritative, so on load OptionDefaults is rebuilt from them.
        var entry = new SessionProfileEntry
        {
            Label = "personal",
            Provider = new ProviderConfigEntry { Provider = SessionProvider.Plugin, PluginProviderId = "claude", PluginConfigJson = "{}" },
            Defaults = new ProfileDefaultsEntry
            {
                PermissionMode = "bypassPermissions",
                Model = "opus",
                Effort = "high",
                OptionDefaults = new Dictionary<string, string> { ["permission-mode"] = "default", ["effort"] = "medium" },
            },
        };

        var profile = entry.ToDomain();

        Assert.Equal("bypassPermissions", profile.Defaults!.OptionDefaults!["permission-mode"]);
        Assert.Equal("opus", profile.Defaults!.OptionDefaults!["model"]);
        Assert.Equal("high", profile.Defaults!.OptionDefaults!["effort"]);
    }

}
