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

        Assert.True(staging.TryStage(view, out var error));
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
        staging.TryStage(view, out _);

        staging.Revert();
        staging.Commit();

        Assert.Equal(0, view.Committed);
        Assert.False(staging.HasStagedChanges);
    }

    [Fact]
    public void ARefusedViewStagesNothing_AndGivesItsOwnReason()
    {
        var staging = new PluginSettingsStaging();

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: "Pick a cluster first."), out var error));

        Assert.Equal("Pick a cluster first.", error);
        Assert.False(staging.HasStagedChanges);
    }

    [Fact]
    public void ARefusalWithNoReason_IsAnsweredRatherThanShownBlank()
    {
        var staging = new PluginSettingsStaging();

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: string.Empty), out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EveryStagedViewIsCommitted_InTheOrderItWasStaged()
    {
        var staging = new PluginSettingsStaging();
        var order = new List<string>();
        staging.TryStage(new OrderedView("first", order), out _);
        staging.TryStage(new OrderedView("second", order), out _);

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
        staging.TryStage(accepted, out _);

        Assert.False(staging.TryStage(new FakeSettingsView(refusal: "no"), out _));

        staging.Commit();
        Assert.Equal(1, accepted.Committed);
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
