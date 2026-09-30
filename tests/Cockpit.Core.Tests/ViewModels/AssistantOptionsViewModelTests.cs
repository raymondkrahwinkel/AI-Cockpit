using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The Options → Voice "Assistant" block (AC-543): the master switch, the Assistant Profile row (a name and an editor, not a picker), the hotkey, and the independent speak-replies switch.</summary>
public class AssistantOptionsViewModelTests
{

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
        Assert.Equal("plugin:cockpit-kubernetes/Kubernetes", plugin.KeyDetail);
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
        Assert.Contains(vm.ConsentBypassSources, row => row.Key == "plugin:Terminal MCP/Terminal MCP");
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
        var store = new FakeSettingsStore(new AssistantSettings { IsEnabled = true, ConsentBypassAll = false });
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
        Assert.False(store.Saved!.ConsentBypassAll, "ticking a row must not switch allow-all back on either");
    }

    // ── AC-637: the "allow all" switch above them ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchingAllowAllOff_PersistsIt_AndKeepsTheRowsAsTheyWere()
    {
        // Off falls back to the granular list rather than to an empty one: the rows are hidden while allow-all is
        // on, never cleared, so this is the operator getting back exactly what they ticked before.
        var store = new FakeSettingsStore(new AssistantSettings
        {
            IsEnabled = true,
            ConsentBypassSources = [ConsentSourceCatalog.TerminalMcp],
        });
        var vm = new AssistantOptionsViewModel(store);
        await vm.RefreshAsync();
        Assert.True(vm.ConsentBypassAll);

        vm.ConsentBypassAll = false;

        Assert.False(store.Saved!.ConsentBypassAll);
        Assert.Equal([ConsentSourceCatalog.TerminalMcp], store.Saved!.ConsentBypassSources);
        Assert.True(vm.ConsentBypassSources.Single(row => row.Key == ConsentSourceCatalog.TerminalMcp).BypassLowRisk);
        Assert.True(store.Saved!.IsEnabled, "the allow-all switch must not disturb the sibling fields");
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
        public Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        // Records nothing: no test here repoints, because the row this file covers is not a picker. What it recorded
        // was picker scaffolding that outlived the picker — set, never asserted, and therefore guarding nothing.
        public Task<AssistantProfileSlot> RepointAsync(
            SessionProfile record,
            bool replacesStandingInstruction,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantProfileSlot(record, null, replacesStandingInstruction));

        public Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantProfileSlot(null, reason));
    }
}
