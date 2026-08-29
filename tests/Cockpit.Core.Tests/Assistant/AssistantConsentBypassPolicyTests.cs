using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// The conditions of #AC-575, one test each and one for every way of failing them, plus #AC-637's "allow all"
/// above them. This is the class that decides whether an operator's consent card appears, so what these hold shut
/// is every path by which "the assistant" quietly becomes "somebody".
/// </summary>
/// <remarks>
/// Most of these run with allow-all off — see <see cref="Enabled"/> — because the per-source lists are what they
/// are about, and it now takes an explicit off to reach them at all.
/// </remarks>
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
            ConsentBypassAll = false,
            ConsentBypassSources = lowRisk ?? [],
            ConsentBypassDangerousSources = dangerous ?? [],
        };

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
        // Allow-all left at its default here on purpose: the widest setting must still take the master switch with it.
        var policy = await PolicyAsync(new AssistantSettings { IsEnabled = false, ConsentBypassSources = [Terminal] });

        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
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
    public async Task DefaultSettings_BypassEverythingTheAssistantAsks()
    {
        // #AC-637 turned this one around: a fresh install has allow-all on, so both risk classes of every source —
        // named by the catalogue or not — go through without a card. The condition above it still holds.
        var policy = await PolicyAsync(new AssistantSettings { IsEnabled = true });

        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));
        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, "a-plugin-nobody-listed", dangerous: true));
        Assert.False(policy.ShouldBypass("pane-ordinary", Terminal, dangerous: false));
    }

    [Fact]
    public async Task WithAllowAllSwitchedOff_OnlyTheTickedSourcesAreBypassed()
    {
        // The other half of the switch: off is the granular list exactly as #AC-575 built it, not an empty one.
        // Conditions 1 and 4 in the same run: the ticked source is bypassed, and a dangerous action on that same
        // source is not — the whole reason there are two checkboxes rather than one three-state picker. A shell
        // command is not "the terminal, but more of it"; it is the decision the operator has not made yet.
        var policy = await PolicyAsync(Enabled(lowRisk: [Terminal]));

        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: false));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));
        Assert.False(policy.ShouldBypass(AssistantIdentity.PaneId, "cockpit-kubernetes", dangerous: false));
    }

    [Fact]
    public async Task SwitchingAllowAllOff_TakesEffectOnTheNextRequest()
    {
        // The snapshot is replaced on the save Options raises, so switching the widest setting off is not a
        // permission that lingers until the next restart.
        var store = new FakeStore(new AssistantSettings { IsEnabled = true });
        var policy = new AssistantConsentBypassPolicy(store);
        await policy.ApplySettingsAsync();
        Assert.True(policy.ShouldBypass(AssistantIdentity.PaneId, Terminal, dangerous: true));

        store.Settings = Enabled();
        await policy.ApplySettingsAsync();

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

        public AssistantSettings Settings { get; set; } = settings;

        public Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Throw ? Task.FromException<AssistantSettings>(new IOException("no")) : Task.FromResult(Settings);

        public Task SaveAsync(AssistantSettings value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
