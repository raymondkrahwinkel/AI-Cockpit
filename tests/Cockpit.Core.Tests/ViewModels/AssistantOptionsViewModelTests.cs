using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;

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

        Assert.False(Assert.Single(vm.ConsentBypassSources, row => row.Key == ConsentSourceCatalog.TerminalMcp).IsOrphan);
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

    [Fact]
    public async Task UntickingASource_RemovesItRatherThanLeavingItOnDisk()
    {
        var store = new FakeSettingsStore(new AssistantSettings { ConsentBypassSources = [ConsentSourceCatalog.TerminalMcp] });
        var vm = new AssistantOptionsViewModel(store);
        await vm.RefreshAsync();

        vm.ContextCompactionPercent = 9;

        vm.ConsentBypassSources.Single(row => row.Key == ConsentSourceCatalog.TerminalMcp).BypassLowRisk = false;

        Assert.Empty(store.Saved!.ConsentBypassSources);
        Assert.Equal(80, store.Saved.ContextCompactionPercent);
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
}
