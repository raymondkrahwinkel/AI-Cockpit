using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The other host of the staged settings contract (AC-1003): the one that does wait. What the Options dialog
/// needs from a plugin view — stage it, hold the write, commit the batch or throw it away on Cancel — asserted
/// without Options, and without a window: no control is involved, only the contract.
/// </summary>
public class PluginSettingsStagingTests
{
    private sealed class FakeSettingsView(string? refusal = null) : IPluginSettingsView
    {
        public int Committed { get; private set; }

        public bool TryStage(out Action? commit, out string? error)
        {
            if (refusal is not null)
            {
                commit = null;
                error = refusal;
                return false;
            }

            commit = () => Committed++;
            error = null;
            return true;
        }
    }

    [Fact]
    public void StagingWritesNothingUntilCommit()
    {
        var staging = new PluginSettingsStaging();
        var view = new FakeSettingsView();

        Assert.True(staging.TryStage(view, onSaved: null, out var error));
        Assert.Null(error);
        Assert.Equal(0, view.Committed);
        Assert.True(staging.HasStagedChanges);

        staging.Commit();

        Assert.Equal(1, view.Committed);
        Assert.False(staging.HasStagedChanges);
    }

    [Fact]
    public void RevertDropsTheStagedWrite_SoCommitAfterwardsWritesNothing()
    {
        var staging = new PluginSettingsStaging();
        var view = new FakeSettingsView();
        staging.TryStage(view, onSaved: null, out _);

        staging.Revert();
        staging.Commit();

        Assert.Equal(0, view.Committed);
        Assert.False(staging.HasStagedChanges);
    }

    [Fact]
    public void ARefusedViewStagesNothing_AndGivesItsOwnReason()
    {
        var staging = new PluginSettingsStaging();

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: "Pick a cluster first."), onSaved: null, out var error));

        Assert.Equal("Pick a cluster first.", error);
        Assert.False(staging.HasStagedChanges);
    }

    [Fact]
    public void ARefusalWithNoReason_IsAnsweredRatherThanShownBlank()
    {
        var staging = new PluginSettingsStaging();

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: string.Empty), onSaved: null, out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EveryStagedViewIsCommitted_InTheOrderItWasStaged()
    {
        var staging = new PluginSettingsStaging();
        var order = new List<string>();
        staging.TryStage(new OrderedView("first", order), onSaved: null, out _);
        staging.TryStage(new OrderedView("second", order), onSaved: null, out _);

        staging.Commit();

        Assert.Equal(["first", "second"], order);
    }

    // A refusal anywhere in the batch is the caller's to act on (Options blocks its whole Apply, as it already
    // does for a profile's provider config) — what this pins is that the refusal itself stages nothing, so a
    // later Commit cannot write half a batch the operator was told was refused.
    [Fact]
    public void ARefusalLeavesTheRestOfTheBatchIntact()
    {
        var staging = new PluginSettingsStaging();
        var accepted = new FakeSettingsView();
        staging.TryStage(accepted, onSaved: null, out _);

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: "no"), onSaved: null, out _));

        staging.Commit();
        Assert.Equal(1, accepted.Committed);
    }

    // AC-1004, criterion 5. The settings-saved signal is what four plugins hang a cache invalidation off
    // (Docker's engine, LocalCi's runtime, Kubernetes' connections, GitHub PR's refresh), so firing it while the
    // values are merely staged would have each rebuild against the settings the operator just replaced — and in
    // Options, where staging and committing are minutes and a Cancel apart, that is not a theoretical gap.
    [Fact]
    public void TheSettingsSavedSignalWaitsForTheCommit_AndFollowsTheWrite()
    {
        var staging = new PluginSettingsStaging();
        var order = new List<string>();
        staging.TryStage(new OrderedView("write", order), onSaved: () => order.Add("notified"), out _);

        Assert.Empty(order);

        staging.Commit();

        Assert.Equal(["write", "notified"], order);
    }

    [Fact]
    public void ARevertedBatchNeverNotifies_BecauseNothingWasWritten()
    {
        var staging = new PluginSettingsStaging();
        var notified = 0;
        staging.TryStage(new FakeSettingsView(), onSaved: () => notified++, out _);

        staging.Revert();
        staging.Commit();

        Assert.Equal(0, notified);
    }

    [Fact]
    public void ARefusedViewNotifiesNothing()
    {
        var staging = new PluginSettingsStaging();
        var notified = 0;

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: "no"), onSaved: () => notified++, out _));

        staging.Commit();
        Assert.Equal(0, notified);
    }

    // One plugin's view refusing must not leave another plugin's subscribers told its settings were saved — the
    // batch Options commits is per view, and so is the signal that follows each write.
    [Fact]
    public void EachViewsSignalRidesWithItsOwnWrite()
    {
        var staging = new PluginSettingsStaging();
        var order = new List<string>();
        staging.TryStage(new OrderedView("first write", order), onSaved: () => order.Add("first notified"), out _);
        staging.TryStage(new OrderedView("second write", order), onSaved: () => order.Add("second notified"), out _);

        staging.Commit();

        Assert.Equal(["first write", "first notified", "second write", "second notified"], order);
    }

    private sealed class OrderedView(string name, List<string> order) : IPluginSettingsView
    {
        public bool TryStage(out Action? commit, out string? error)
        {
            commit = () => order.Add(name);
            error = null;
            return true;
        }
    }
}
