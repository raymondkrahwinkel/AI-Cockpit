using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Core.Tests.ViewModels.Onboarding;

/// <summary>
/// One provider row on the first-run wizard's provider step (AC-510[b]). Criterion 1 (found shown differently
/// from not-found, never claiming "works") and criterion 2 (the seam's four outcomes, each with its own line and
/// colour) both land here — the view only binds to what this class already computed.
/// </summary>
public class ProviderPickerRowViewModelTests
{
    private static readonly PluginStoreConfig Store = PluginStoreConfig.Remote("https://example.com/index.json");

    private static PluginStoreEntry _Entry(int abstractionsVersion = AbstractionsContract.Version) => new(
        "claude-provider", "Claude Code", "d", "Cockpit", "1.0.0",
        [new PluginStoreVersion("1.0.0", "claude-provider-1.0.0.zip", abstractionsVersion, null, null, null)],
        Category: PluginStoreEntry.ProviderCategory);

    // --- Criterion 1: found vs not-found is a visibly different pill, and the label never promises "works". -----

    [Fact]
    public void DetectionLabel_Found_SaysFoundOnly_NotThatItWorks()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        Assert.Equal("Found on this machine", row.DetectionLabel);
        Assert.DoesNotContain("work", row.DetectionLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logged in", row.DetectionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectionLabel_NotFound_DiffersFromFound_InTextAndBrush()
    {
        var found = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);
        var notFound = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.NotFound);

        Assert.NotEqual(found.DetectionLabel, notFound.DetectionLabel);
        Assert.NotEqual(found.DetectionBrushKey, notFound.DetectionBrushKey);
    }

    [Fact]
    public void NotApplicable_HidesTheDetectionPillEntirely_RatherThanClaimingFoundOrNotFound()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.NotApplicable);

        Assert.False(row.ShowDetectionPill);
    }

    // --- Criterion 4: a found CLI provider suggests itself, but that is a suggestion, never a decision already
    // made — everything else starts unselected. ------------------------------------------------------------------

    [Fact]
    public void Found_AndCompatible_StartsSelected_AsASuggestion()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        Assert.True(row.IsSelected);
    }

    [Fact]
    public void NotFound_StartsUnselected()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.NotFound);

        Assert.False(row.IsSelected);
    }

    [Fact]
    public void Incompatible_StartsUnselected_AndCannotBeSelected()
    {
        // hostAbstractionsMajor=2 while the entry's only version declares 1 -> IsIncompatible.
        var row = new ProviderPickerRowViewModel(
            new StorePluginRowViewModel(_Entry(abstractionsVersion: 1), Store, installedVersion: null, hostAbstractionsMajor: 2),
            ProviderDetectionState.Found);

        Assert.True(row.Row.IsIncompatible);
        Assert.False(row.IsSelected);
        Assert.False(row.CanSelect);
    }

    [Fact]
    public void AlreadyInstalled_StartsUnselected_ButStillSelectable()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: "1.0.0"), ProviderDetectionState.Found);

        Assert.False(row.IsSelected);
        Assert.True(row.CanSelect);
    }

    // --- Criterion 2: the provisioning seam's four outcomes, each landing on its own line. ------------------------

    [Fact]
    public void ApplyOutcome_Installed_SaysItStillNeedsApproval_RatherThanClaimingItRuns()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        row.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Installed, "claude-provider", "Claude Code", null, null, "claude-provider", "sha"));

        Assert.True(row.HasOutcome);
        Assert.Contains("plugin store", row.OutcomeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyOutcome_Staged_NamesTheAlreadyInstalledCase_AndTheRestart()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: "0.9.0"), ProviderDetectionState.Found);

        row.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Staged, "claude-provider", "Claude Code", null, null, "claude-provider", "sha"));

        Assert.Contains("already installed", row.OutcomeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", row.OutcomeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyOutcome_Incompatible_CarriesTheSeamsOwnReason()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        row.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Incompatible, "claude-provider", "Claude Code", "needs contract version 2", null, null, null));

        Assert.Contains("needs contract version 2", row.OutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyOutcome_Failed_CarriesTheError_ForOfflineOrAnyOtherFailure()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        row.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Failed, "claude-provider", "Claude Code", "the store could not be reached", null, null, null));

        Assert.Contains("the store could not be reached", row.OutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyOutcome_EachOfTheFourOutcomes_GetsItsOwnLine_AndTheTwoFailureShapesReadAsFailure()
    {
        PluginProvisionResult Result(PluginProvisionOutcome outcome) => new(outcome, "id", "name", "err", null, "id", "sha");

        var installed = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, null), ProviderDetectionState.Found);
        var staged = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, null), ProviderDetectionState.Found);
        var incompatible = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, null), ProviderDetectionState.Found);
        var failed = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, null), ProviderDetectionState.Found);

        installed.ApplyOutcome(Result(PluginProvisionOutcome.Installed));
        staged.ApplyOutcome(Result(PluginProvisionOutcome.Staged));
        incompatible.ApplyOutcome(Result(PluginProvisionOutcome.Incompatible));
        failed.ApplyOutcome(Result(PluginProvisionOutcome.Failed));

        // Four distinct lines of text — nothing here collapses two outcomes into the same wording.
        var texts = new[] { installed.OutcomeText, staged.OutcomeText, incompatible.OutcomeText, failed.OutcomeText };
        Assert.Equal(4, texts.Distinct(StringComparer.Ordinal).Count());

        // Incompatible and Failed both read as failure (same error brush); Installed and Staged do not share it.
        Assert.Equal(incompatible.OutcomeBrushKey, failed.OutcomeBrushKey);
        Assert.NotEqual(incompatible.OutcomeBrushKey, installed.OutcomeBrushKey);
        Assert.NotEqual(incompatible.OutcomeBrushKey, staged.OutcomeBrushKey);
        Assert.NotEqual(installed.OutcomeBrushKey, staged.OutcomeBrushKey);
    }

    [Fact]
    public void HasOutcome_IsFalse_UntilApplyOutcomeIsCalled()
    {
        var row = new ProviderPickerRowViewModel(new StorePluginRowViewModel(_Entry(), Store, installedVersion: null), ProviderDetectionState.Found);

        Assert.False(row.HasOutcome);
    }
}
