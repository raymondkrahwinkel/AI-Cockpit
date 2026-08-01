using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The Options → Voice "Assistant" block (AC-543): the master switch, the Assistant Profile picker, the hotkey, and the independent speak-replies switch.</summary>
public class AssistantOptionsViewModelTests
{
    // Decision 7 / criterion 1: a fresh dialog with nothing saved yet reads as off, before any store is even asked.
    [Fact]
    public void Constructed_WithNoStores_StartsDisabled()
    {
        var vm = new AssistantOptionsViewModel();

        Assert.False(vm.IsEnabled);
    }

    [Fact]
    public async Task TogglingIsEnabled_PersistsThroughTheSettingsStore_WithoutTouchingSiblingFields()
    {
        // The indicator (a different strand) owns AlwaysOnCostAcknowledged; a save from this view model must not reset it.
        var settingsStore = new FakeSettingsStore(new AssistantSettings { AlwaysOnCostAcknowledged = true });
        var vm = new AssistantOptionsViewModel(settingsStore);
        await vm.RefreshAsync();

        vm.IsEnabled = true;

        Assert.True(settingsStore.Saved!.IsEnabled);
        Assert.True(settingsStore.Saved!.AlwaysOnCostAcknowledged);
    }

    // Criterion 9: speaking and being enabled are two separate switches.
    [Fact]
    public async Task TogglingSpeakReplies_DoesNotChangeIsEnabled()
    {
        var settingsStore = new FakeSettingsStore(new AssistantSettings { IsEnabled = true });
        var vm = new AssistantOptionsViewModel(settingsStore);
        await vm.RefreshAsync();

        vm.SpeakReplies = false;

        Assert.False(settingsStore.Saved!.SpeakReplies);
        Assert.True(settingsStore.Saved!.IsEnabled);
    }

    [Fact]
    public async Task RefreshAsync_ShowsTheSlotsFixedName_NotTheRecordsLabel()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(new SessionProfile("My Claude login", new ClaudeConfig("/tmp"))));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);

        await vm.RefreshAsync();

        Assert.Equal("Assistant Profile", AssistantOptionsViewModel.ProfileSlotDisplayName);
        Assert.Equal("My Claude login", vm.SelectedProfile!.Label);
    }

    [Fact]
    public async Task RefreshAsync_WhenSlotIsUnset_SurfacesTheReason()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(null, "The Codex switch failed: no API key."));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);

        await vm.RefreshAsync();

        Assert.Null(vm.SelectedProfile);
        Assert.Equal("The Codex switch failed: no API key.", vm.ProfileUnsetReason);
    }

    [Fact]
    public async Task SelectingAProfile_RepointsTheSlot()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(null, "not set up yet"));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);
        await vm.RefreshAsync();
        var chosen = new SessionProfile("Codex", new ClaudeConfig("/tmp"));

        vm.SelectedProfile = chosen;
        await vm.PendingProfileRepoint!;

        Assert.Equal(chosen, profileStore.RepointedTo);
        Assert.Null(vm.ProfileUnsetReason);
    }

    // ── The slot holds a copy, and that copy must not go stale in silence ─────────────────────────────────────
    //
    // Measured on a real config: Profiles → 'default' had OptionDefaults { permission-mode: bypassPermissions },
    // while the Assistant Profile's copy of that same record still said { permission-mode: default }. The slot
    // carries a whole SessionProfile taken when it was picked and follows no later edit — deliberately, because
    // being found by nothing is what stops a rename or a delete cutting the assistant loose (AC-410). What was
    // missing was that the price was written down anywhere, and a way to pay it.

    [Fact]
    public async Task RefreshAsync_WithAProfileSet_SaysOutLoudThatTheAssistantRunsOnACopyOfIt()
    {
        var vm = new AssistantOptionsViewModel(
            profileStore: new FakeProfileStore(new AssistantProfileSlot(_Profile("default", "default"))),
            sessionProfileStore: new FakeSessionProfileStore(_Profile("default", "bypassPermissions")));

        await vm.RefreshAsync();

        // The exact wording is not the contract; that it names the record and says a copy is, because a page that
        // shows only a dropdown reads as "the assistant uses this profile" — which is the belief that cost the
        // operator an afternoon.
        Assert.NotNull(vm.ProfileSnapshotNote);
        Assert.Contains("copy", vm.ProfileSnapshotNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", vm.ProfileSnapshotNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_WithNoProfileSet_SaysNothingAboutACopy()
    {
        // There is nothing to have copied, and ProfileUnsetReason is what the page shows instead. A note here
        // would be a sentence about a record that does not exist.
        var vm = new AssistantOptionsViewModel(
            profileStore: new FakeProfileStore(new AssistantProfileSlot(null, "not set up yet")));

        await vm.RefreshAsync();

        Assert.Null(vm.ProfileSnapshotNote);
        Assert.False(vm.CanRefreshProfileSnapshot);
    }

    /// <summary>
    /// The whole of part B: an edit to the profile the copy was taken from reaches the assistant, in one click.
    /// </summary>
    [Fact]
    public async Task RefreshingTheSnapshot_RepointsTheSlotAtTheEditedRecord()
    {
        var stale = _Profile("default", "default");
        var edited = _Profile("default", "bypassPermissions");
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(stale));
        var vm = new AssistantOptionsViewModel(
            profileStore: profileStore,
            sessionProfileStore: new FakeSessionProfileStore(edited));
        await vm.RefreshAsync();

        Assert.True(vm.CanRefreshProfileSnapshot);
        await vm.RefreshProfileSnapshotCommand.ExecuteAsync(null);

        // Not "a profile named default" — the record that is on disk now, permission mode and all.
        Assert.Same(edited, profileStore.RepointedTo);
        Assert.Equal(
            "bypassPermissions",
            profileStore.RepointedTo!.Defaults!.OptionDefaults!["permission-mode"]);
    }

    /// <summary>
    /// AC-410's protection, intact. The label is consulted in exactly one place — to offer this button a candidate
    /// — and never to resolve the slot. So a renamed (or deleted) profile costs the button and nothing else: the
    /// slot still holds its record, the assistant still runs, and no write happens behind the operator's back.
    /// </summary>
    [Fact]
    public async Task WhenTheProfileWasRenamedAway_TheSlotKeepsItsRecord_AndNothingIsRepointed()
    {
        var held = _Profile("default", "bypassPermissions");
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(held));
        var vm = new AssistantOptionsViewModel(
            profileStore: profileStore,
            // The same profile, renamed. On a label lookup that could lose, this is where the assistant would read
            // as unconfigured — the AC-410 bug, exactly.
            sessionProfileStore: new FakeSessionProfileStore(_Profile("default (old)", "bypassPermissions")));

        await vm.RefreshAsync();

        Assert.Same(held, vm.SelectedProfile);
        Assert.Null(vm.ProfileUnsetReason);
        Assert.False(vm.CanRefreshProfileSnapshot);
        Assert.Null(profileStore.RepointedTo);

        // And it still says what it is, rather than falling silent on the case where refreshing is impossible.
        Assert.NotNull(vm.ProfileSnapshotNote);
    }

    /// <summary>
    /// Opening the page must not repoint anything on its own. Doing so silently would mean a profile renamed away
    /// and a <em>new</em> one created under the old name quietly becomes the assistant's — the impostor case that
    /// is precisely why the slot does not resolve by name.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NeverRepointsTheSlotByItself()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(_Profile("default", "default")));
        var vm = new AssistantOptionsViewModel(
            profileStore: profileStore,
            sessionProfileStore: new FakeSessionProfileStore(_Profile("default", "bypassPermissions")));

        await vm.RefreshAsync();

        Assert.Null(profileStore.RepointedTo);
    }

    private static SessionProfile _Profile(string label, string permissionMode) =>
        new(label, new ClaudeConfig("/tmp"))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["permission-mode"] = permissionMode,
                },
            },
        };

    private sealed class FakeSessionProfileStore(params SessionProfile[] profiles) : Cockpit.Core.Abstractions.Profiles.ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>(profiles);

        public Task SaveAsync(IReadOnlyList<SessionProfile> saved, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Assistant page never writes the profile list.");
    }

    // ── AC-575: the consent-bypass rows ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsentBypassRows_ComeFromTheHostsOwnSources_AndFromWhatHasActuallyAsked()
    {
        // Never free text. The cockpit's own callers come from the catalogue the gates themselves build their
        // ConsentSource from; a plugin has no compile-time entry anywhere, so it appears because it asked — keyed
        // on the id the host stamped, not on the label the plugin chose for itself.
        var vm = new AssistantOptionsViewModel(
            new FakeSettingsStore(new AssistantSettings()),
            consentAuditLog: new FakeConsentAuditLog(
                Audit(pluginId: "cockpit-kubernetes", label: "Kubernetes"),
                Audit(pluginId: null, label: ConsentSourceCatalog.TerminalMcp)));

        await vm.RefreshAsync();

        Assert.Contains(vm.ConsentBypassSources, row => row.Key == ConsentSourceCatalog.TerminalMcp);
        Assert.Contains(vm.ConsentBypassSources, row => row.Key == ConsentSourceCatalog.Orchestrator);

        // Under the prefix the broker also builds its key with, so a plugin id and a host label are never one row.
        var plugin = Assert.Single(vm.ConsentBypassSources, row => row.Key == ConsentSourceCatalog.KeyFor("cockpit-kubernetes", "Kubernetes"));
        Assert.Equal("Kubernetes", plugin.Label);
        Assert.Equal("plugin:cockpit-kubernetes", plugin.KeyDetail);
    }

    [Fact]
    public async Task ConsentBypassRows_KeyOnTheStampedId_SoAPluginCannotBorrowACockpitSourcesRow()
    {
        // The label is the plugin's own text; the key is the host's. The sharp case is a plugin whose stamped
        // *manifest id* is itself a host label — the host stamps it faithfully, so on a flat key space it lands on
        // the row the operator ticked for the cockpit's own terminal gate. Prefixing the plugin half keeps them two
        // rows and two switches.
        var vm = new AssistantOptionsViewModel(
            new FakeSettingsStore(new AssistantSettings()),
            consentAuditLog: new FakeConsentAuditLog(
                Audit(pluginId: ConsentSourceCatalog.TerminalMcp, label: ConsentSourceCatalog.TerminalMcp)));

        await vm.RefreshAsync();

        Assert.Equal(2, vm.ConsentBypassSources.Count(row => row.Label == ConsentSourceCatalog.TerminalMcp));
        Assert.Contains(vm.ConsentBypassSources, row => row.Key == "plugin:Terminal MCP");
        Assert.Contains(vm.ConsentBypassSources, row => row.Key == ConsentSourceCatalog.TerminalMcp);
    }

    [Fact]
    public async Task ConsentBypassRows_IncludeASwitchedOnSourceThisBuildKnowsNothingAbout()
    {
        // The plugin has been uninstalled, or the name predates this build. Without a row the permission would be
        // set, in force, and invisible — the one state a security setting may never be in.
        var store = new FakeSettingsStore(new AssistantSettings { ConsentBypassDangerousSources = ["a-plugin-since-removed"] });
        var vm = new AssistantOptionsViewModel(store);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.ConsentBypassSources, candidate => candidate.Key == "a-plugin-since-removed");
        Assert.True(row.BypassDangerous);
        Assert.False(row.BypassLowRisk);

        // #K11: a stored key this build no longer recognises (neither the catalogue nor the trail names it) —
        // most often left behind by a source's own id changing underneath it — still gets a switchable row, but
        // marked as a leftover so it does not read as a second, live source next to the real one.
        Assert.True(row.IsOrphan);
    }

    [Fact]
    public async Task ConsentBypassRows_ARecognisedSource_IsNeverMarkedAsALeftover()
    {
        var vm = new AssistantOptionsViewModel(new FakeSettingsStore(new AssistantSettings()));

        await vm.RefreshAsync();

        var terminal = vm.ConsentBypassSources.Single(row => row.Key == ConsentSourceCatalog.TerminalMcp);
        Assert.False(terminal.IsOrphan);
    }

    [Fact]
    public async Task TickingDangerous_PersistsOnlyDangerous_AndLeavesTheEverydaySwitchAlone()
    {
        var store = new FakeSettingsStore(new AssistantSettings { IsEnabled = true });
        var vm = new AssistantOptionsViewModel(store);
        await vm.RefreshAsync();

        Assert.False(vm.HasConsentBypass);

        var terminal = vm.ConsentBypassSources.Single(row => row.Key == ConsentSourceCatalog.TerminalMcp);
        terminal.BypassDangerous = true;

        Assert.Equal([ConsentSourceCatalog.TerminalMcp], store.Saved!.ConsentBypassDangerousSources);
        Assert.Empty(store.Saved!.ConsentBypassSources);
        Assert.False(terminal.BypassLowRisk);
        Assert.True(vm.HasConsentBypass);
        Assert.True(store.Saved!.IsEnabled, "a bypass tick must not disturb the sibling fields");
    }

    [Fact]
    public async Task UntickingASource_RemovesItRatherThanLeavingItOnDisk()
    {
        var store = new FakeSettingsStore(new AssistantSettings { ConsentBypassSources = [ConsentSourceCatalog.TerminalMcp] });
        var vm = new AssistantOptionsViewModel(store);
        await vm.RefreshAsync();

        vm.ConsentBypassSources.Single(row => row.Key == ConsentSourceCatalog.TerminalMcp).BypassLowRisk = false;

        Assert.Empty(store.Saved!.ConsentBypassSources);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotSaveTheRowsBackOut()
    {
        // Seeding the checkboxes from disk must not read as the operator ticking them — the same guard the other
        // switches on this page already have, and the one that would otherwise rewrite the file on every open.
        var store = new FakeSettingsStore(new AssistantSettings { ConsentBypassSources = [ConsentSourceCatalog.TerminalMcp] });

        await new AssistantOptionsViewModel(store).RefreshAsync();

        Assert.Null(store.Saved);
    }

    private static ConsentAuditEntry Audit(string? pluginId, string label) =>
        new(DateTimeOffset.UtcNow, ConsentAuditAction.Approved, label, "pane-1", pluginId, "scope", "action", Remembered: false);

    private sealed class FakeConsentAuditLog(params ConsentAuditEntry[] entries) : IConsentAuditLog
    {
        public Task RecordAsync(ConsentAuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Options page never writes the consent trail.");

        public Task<IReadOnlyList<ConsentAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsentAuditEntry>>(entries);
    }

    private sealed class FakeSettingsStore(AssistantSettings initial) : IAssistantSettingsStore
    {
        public AssistantSettings? Saved { get; private set; }

        public Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProfileStore(AssistantProfileSlot initial) : IAssistantProfileStore
    {
        public SessionProfile? RepointedTo { get; private set; }

        public Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        public Task<AssistantProfileSlot> RepointAsync(SessionProfile record, CancellationToken cancellationToken = default)
        {
            RepointedTo = record;
            return Task.FromResult(new AssistantProfileSlot(record));
        }

        public Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantProfileSlot(null, reason));
    }
}
