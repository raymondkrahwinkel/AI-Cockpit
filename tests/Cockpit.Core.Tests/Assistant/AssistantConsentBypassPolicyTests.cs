using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The four conditions of #AC-575, one test each and one for every way of failing them. This is the class that
/// decides whether an operator's consent card appears, so what these hold shut is every path by which "the
/// assistant, for this source" quietly becomes "somebody, for something".
/// </summary>
public sealed class AssistantConsentBypassPolicyTests
{
    private const string Terminal = ConsentSourceCatalog.TerminalMcp;

    private static async Task<AssistantConsentBypassPolicy> PolicyAsync(AssistantSettings settings)
    {
        var policy = new AssistantConsentBypassPolicy(new FakeStore(settings));

        // The constructor starts its own load; awaiting one here makes the snapshot deterministic rather than
        // racing the test.
        await policy.ApplySettingsAsync();
        return policy;
    }

    private static AssistantSettings Enabled(
        IReadOnlyList<string>? lowRisk = null, IReadOnlyList<string>? dangerous = null) => new()
        {
            IsEnabled = true,
            ConsentBypassSources = lowRisk ?? [],
            ConsentBypassDangerousSources = dangerous ?? [],
        };

    [Fact]
    public async Task TheAssistantsOwnPane_ASwitchedOnSource_ALowRiskAction_IsBypassed()
    {
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
    }

    [Fact]
    public async Task AnOrdinaryPane_IsNeverBypassed_EvenForASwitchedOnSource()
    {
        // Condition 1. The switch belongs to the assistant, not to the source: an ordinary agent session asking the
        // same thing of the same source is asked about exactly as it was before this feature existed.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.False(policy.ShouldBypass("pane-ordinary", Terminal, dangerous: false));
    }

    [Fact]
    public async Task ARequestWithNoVerifiedPane_IsNeverBypassed()
    {
        // The in-process tool loop and the app's own UI-side consent arrive with no verified session at all. "I
        // cannot tell who this is" is not an identity, so it can never be the assistant's — and this is also where
        // a forged Source.PaneId lands, because the broker only ever hands the transport-stamped id to this method.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.False(policy.ShouldBypass(null, Terminal, dangerous: false));
    }

    [Fact]
    public async Task WithTheAssistantSwitchedOff_NothingIsBypassed()
    {
        // Condition 2. A standing exemption belonging to a feature that is off is a permission nobody is watching.
        var policy = await PolicyAsync(new AssistantSettings { IsEnabled = false, ConsentBypassSources = [Terminal] });

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
    }

    [Fact]
    public async Task ASourceTheOperatorNeverSwitchedOn_IsNotBypassed()
    {
        // Condition 3, and the reason the switch is per source rather than one master button: switching the
        // terminal off must say nothing at all about kubernetes.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, "cockpit-kubernetes", dangerous: false));
    }

    [Fact]
    public async Task TheEverydaySwitch_DoesNotCoverADangerousAction()
    {
        // Condition 4, and the whole reason there are two checkboxes instead of one three-state picker. A shell
        // command is not "the terminal, but more of it" — it is the decision the operator has not made yet.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));
    }

    [Fact]
    public async Task TheDangerousSwitch_StandsOnItsOwn()
    {
        // The other direction of the same independence: it is not implied by the everyday one, and it does not
        // imply it either. Each answers only the risk it was ticked for.
        var policy = await PolicyAsync(Enabled(dangerous: [Terminal]));

        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
    }

    [Fact]
    public async Task SourcesAreMatchedExactly_NotLoosely()
    {
        // Sources are never typed by a human — the Options list is filled from host-stamped names — so a looser
        // match could only ever widen what counts as the same source, and widening is the direction that costs.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, "terminal mcp", dangerous: false));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, "Terminal MCP ", dangerous: false));
    }

    [Fact]
    public async Task DefaultSettings_BypassNothing()
    {
        // A fresh install, and the state the snapshot starts in before its first load has landed.
        var policy = await PolicyAsync(new AssistantSettings { IsEnabled = true });

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));
    }

    [Fact]
    public async Task AnUnreadableStore_BypassesNothing_RatherThanKeepingTheOlderWiderSnapshot()
    {
        // Fail-closed on a re-read, not just on the first one: an operator who has just unticked a source and hit a
        // failing write must not be left with the previous, wider set still in force.
        var store = new FakeStore(Enabled(lowRisk: [Terminal]));
        var policy = new AssistantConsentBypassPolicy(store);
        await policy.ApplySettingsAsync();
        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));

        store.Throw = true;
        await policy.ApplySettingsAsync();

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
    }

    private sealed class FakeStore(AssistantSettings settings) : IAssistantSettingsStore
    {
        public bool Throw { get; set; }

        public Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Throw ? Task.FromException<AssistantSettings>(new IOException("no")) : Task.FromResult(settings);

        public Task SaveAsync(AssistantSettings value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
