using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The four acceptance criteria the Assistant Profile slot carries (AC-543 2-5), against a real temporary
/// <c>cockpit.json</c> — the store is pointed at it through its internal test seam, like
/// <c>SessionProfileStoreTests</c>.
/// </summary>
public class AssistantProfileStoreTests : IDisposable
{
    private const string ClaudeConfigDir = @"C:\Users\raymo\.claude";
    private const string CodexProviderId = "codex-provider.codex";

    private readonly string _tempDir;
    private readonly string _configFilePath;

    public AssistantProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    /// <summary>
    /// Criterion 2: renaming the record behind the slot does not make the profile unfindable, and there is no
    /// public way to delete the slot at all.
    /// </summary>
    /// <remarks>
    /// AC-410's live bug is that "a profile is matched by label, so a rename reads as 'gone'". The slot resolves
    /// through its own config section instead, so a rename is just a rename — this test fails the moment anyone
    /// reintroduces label matching on this path.
    /// </remarks>
    [Fact]
    public async Task RenamingTheRecord_LeavesTheSlotResolvable_AndNothingCanDeleteIt()
    {
        var store = new AssistantProfileStore(_configFilePath);
        var record = new SessionProfile("Assistant (Claude)", ClaudePluginProfile.Create(ClaudeConfigDir, null));

        await store.RepointAsync(record, replacesStandingInstruction: false);
        await store.RepointAsync(record with { Label = "Something else entirely" }, replacesStandingInstruction: false);

        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsConfigured);
        Assert.Null(loaded.UnsetReason);
        Assert.Equal("Something else entirely", loaded.Profile!.Label);

        // The slot is undeletable because nothing offers to delete it — a guard could be forgotten or bypassed,
        // an absent method cannot be called. If a delete ever lands here, this is what says so.
        Assert.DoesNotContain(
            typeof(IAssistantProfileStore).GetMethods(),
            method => method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Clear", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Criterion 3: switching provider works both ways, and no <see cref="SessionProfile"/> record ever changes
    /// provider — each switch mints a new record.
    /// </summary>
    /// <remarks>
    /// Asserting only "after the switch the slot says Codex" passes on the forbidden implementation too
    /// (<c>record with { ProviderConfig = codex }</c>), which is why this compares record <em>identity</em>: each
    /// record carries a label of its own, and the slot must come back carrying the whole new record rather than
    /// the previous one with its provider swapped. Going back to Claude gets a third record, not the first one
    /// resurrected — and every instance still reports the provider it was created with afterwards.
    /// </remarks>
    [Fact]
    public async Task SwitchingProvider_MintsANewRecordEachTime_AndNoRecordEverChangesProvider()
    {
        var store = new AssistantProfileStore(_configFilePath);
        var claude = new SessionProfile("assistant-claude-1", ClaudePluginProfile.Create(ClaudeConfigDir, null));
        var codex = new SessionProfile("assistant-codex", new PluginProviderConfig(CodexProviderId, """{"model":"gpt-5-codex"}"""));
        var claudeAgain = new SessionProfile("assistant-claude-2", ClaudePluginProfile.Create(ClaudeConfigDir, null));

        await store.RepointAsync(claude, replacesStandingInstruction: false);
        var afterClaude = await store.LoadAsync();

        await store.RepointAsync(codex, replacesStandingInstruction: false);
        var afterCodex = await store.LoadAsync();

        await store.RepointAsync(claudeAgain, replacesStandingInstruction: false);
        var afterBack = await store.LoadAsync();

        // Each load returns the record that was handed over, whole. The label is the tell: the forbidden
        // implementation would still be showing "assistant-claude-1" after the switch to Codex.
        Assert.Equal("assistant-claude-1", afterClaude.Profile!.Label);
        Assert.Equal("assistant-codex", afterCodex.Profile!.Label);
        Assert.Equal("assistant-claude-2", afterBack.Profile!.Label);

        Assert.Equal(ClaudePluginProfile.ProviderId, _ProviderIdOf(afterClaude));
        Assert.Equal(CodexProviderId, _ProviderIdOf(afterCodex));
        Assert.Equal(ClaudePluginProfile.ProviderId, _ProviderIdOf(afterBack));

        // Going back to Claude is a third record, not the first one reused or repaired.
        Assert.NotSame(claude, claudeAgain);
        Assert.NotEqual(claude.Label, claudeAgain.Label);

        // And every record instance still describes the backend it was minted for, after three switches.
        Assert.Equal(ClaudePluginProfile.ProviderId, ((PluginProviderConfig)claude.ProviderConfig).ProviderId);
        Assert.Equal(CodexProviderId, ((PluginProviderConfig)codex.ProviderConfig).ProviderId);
        Assert.Equal(ClaudePluginProfile.ProviderId, ((PluginProviderConfig)claudeAgain.ProviderConfig).ProviderId);
    }

    /// <summary>
    /// Criterion 4: a failed switch never leaves an empty, unexplained slot — the old record stays, or the slot
    /// lands on "not set up" with the reason attached.
    /// </summary>
    [Fact]
    public async Task AFailedSwitch_KeepsTheOldRecord_OrLandsUnsetWithAReason()
    {
        var store = new AssistantProfileStore(_configFilePath);
        var claude = new SessionProfile("assistant-claude", ClaudePluginProfile.Create(ClaudeConfigDir, null));
        await store.RepointAsync(claude, replacesStandingInstruction: false);

        // A switch that blows up while minting the new record never reaches the store, so nothing was written.
        try
        {
            await store.RepointAsync(_MintRecordThatFails(), replacesStandingInstruction: false);
        }
        catch (InvalidOperationException)
        {
            // The switch failed, which is the point of this arm.
        }

        var afterFailure = await store.LoadAsync();
        Assert.True(afterFailure.IsConfigured);
        Assert.Equal("assistant-claude", afterFailure.Profile!.Label);

        // The other permitted landing place: explicitly unset, with words the operator can act on.
        var afterGivingUp = await store.UnsetAsync("Codex is not signed in on this machine.");
        Assert.False(afterGivingUp.IsConfigured);
        Assert.Equal("Codex is not signed in on this machine.", afterGivingUp.UnsetReason);
        Assert.Equal(afterGivingUp, await store.LoadAsync());

        // A blank reason is refused rather than stored: it would produce exactly the unexplained empty slot this
        // criterion rules out, only with the emptiness one level down where nobody looks.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.UnsetAsync("  "));

        // And a config hand-edited into the forbidden state still reads back as a slot that says something.
        await File.WriteAllTextAsync(_configFilePath, """{"AssistantProfile":{"Profile":null,"UnsetReason":null}}""");
        var repaired = await new AssistantProfileStore(_configFilePath).LoadAsync();
        Assert.False(repaired.IsConfigured);
        Assert.False(string.IsNullOrWhiteSpace(repaired.UnsetReason));
    }

    /// <summary>
    /// Criterion 5: the Assistant Profile does not appear in <em>+ New session</em> and is not a delegation
    /// target in <c>list_profiles</c>.
    /// </summary>
    /// <remarks>
    /// Both surfaces build their list from <c>ISessionProfileStore.LoadAsync</c> — <c>NewSessionDialogViewModel</c>
    /// takes it unfiltered, <c>DelegationService.ListTargetsAsync</c> filters it on
    /// <see cref="DelegationPolicy.AllowedAsTarget"/> — so that store is the real source to test against rather
    /// than either view. The slot record here is deliberately given a policy that <em>would</em>
    /// make it a delegation target if it were in the list: it is kept out by living in another section, not by a
    /// filter somebody has to remember.
    /// </remarks>
    [Fact]
    public async Task TheAssistantProfile_IsNotInWhatTheSessionProfileStoreReturns()
    {
        var profileStore = new SessionProfileStore(_configFilePath);
        await profileStore.SaveAsync([new SessionProfile("work", ClaudePluginProfile.Create(ClaudeConfigDir, null))]);

        var assistantStore = new AssistantProfileStore(_configFilePath);
        await assistantStore.RepointAsync(new SessionProfile(
            AssistantProfileSlot.DisplayName,
            ClaudePluginProfile.Create(ClaudeConfigDir, null),
            Delegation: new DelegationPolicy(AllowedAsTarget: true)), replacesStandingInstruction: false);

        var visibleProfiles = await profileStore.LoadAsync();

        Assert.Equal("work", Assert.Single(visibleProfiles).Label);
        Assert.DoesNotContain(visibleProfiles, profile => profile.DelegationPolicy.AllowedAsTarget);

        // It is stored and resolvable all the same — invisible to those two lists, not missing.
        Assert.True((await assistantStore.LoadAsync()).IsConfigured);
    }

    /// <summary>
    /// AC-594: the advanced "replace the built-in instructions" choice survives a write and a read, and a config
    /// written before it existed loads as adding rather than replacing.
    /// </summary>
    [Fact]
    public async Task WhetherTheInstructionReplacesTheBuiltInOne_RoundTrips_AndAnOlderConfigReadsAsAdding()
    {
        var store = new AssistantProfileStore(_configFilePath);
        var record = new SessionProfile("Assistant (Claude)", ClaudePluginProfile.Create(ClaudeConfigDir, null))
        {
            SystemPrompt = "Your name is Zyra.",
        };

        await store.RepointAsync(record, replacesStandingInstruction: true);
        Assert.True((await store.LoadAsync()).ReplacesStandingInstruction);

        await store.RepointAsync(record, replacesStandingInstruction: false);
        Assert.False((await store.LoadAsync()).ReplacesStandingInstruction);

        // A config written before this setting existed: the key is gone entirely, and that has to read as adding
        // rather than as an assistant whose built-in instructions were replaced by whatever is in the box.
        await store.RepointAsync(record, replacesStandingInstruction: true);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath));
        var assistantSection = config?[nameof(CockpitConfigFile.AssistantProfile)]?.AsObject();
        Assert.NotNull(assistantSection);
        Assert.True(assistantSection.Remove(nameof(AssistantProfileSlot.ReplacesStandingInstruction)));
        await File.WriteAllTextAsync(_configFilePath, config?.ToJsonString());

        var loaded = await store.LoadAsync();
        Assert.True(loaded.IsConfigured);
        Assert.False(loaded.ReplacesStandingInstruction);
    }

    private static string _ProviderIdOf(AssistantProfileSlot slot) =>
        Assert.IsType<PluginProviderConfig>(slot.Profile!.ProviderConfig).ProviderId;

    /// <summary>Stands in for a provider switch that cannot produce a usable record — a login that is not there, a plugin that failed to load.</summary>
    private static SessionProfile _MintRecordThatFails() =>
        throw new InvalidOperationException("Codex is not signed in on this machine.");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
