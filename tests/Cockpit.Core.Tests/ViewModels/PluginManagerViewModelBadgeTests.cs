using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The sidebar "Plugin store" update badge (AC-76): its count drives whether the badge shows, and a change notifies both.</summary>
public class PluginManagerViewModelBadgeTests
{
    [Fact]
    public void HasUpdateBadge_ReflectsTheCount()
    {
        var vm = new PluginManagerViewModel();
        Assert.False(vm.HasUpdateBadge);

        vm.UpdateBadgeCount = 3;
        Assert.True(vm.HasUpdateBadge);

        vm.UpdateBadgeCount = 0;
        Assert.False(vm.HasUpdateBadge);
    }

    [Fact]
    public void UpdateBadgeCount_Change_NotifiesCountAndBadge()
    {
        var vm = new PluginManagerViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.UpdateBadgeCount = 2;

        Assert.Contains(nameof(PluginManagerViewModel.UpdateBadgeCount), raised);
        Assert.Contains(nameof(PluginManagerViewModel.HasUpdateBadge), raised);
    }

    /// <summary>The AC-208 pending-approval badge: no rows at all, so no plugin can be awaiting approval.</summary>
    [Fact]
    public void NoPlugins_HasPendingApprovalIsFalse()
    {
        var vm = new PluginManagerViewModel();

        Assert.Equal(0, vm.PendingApprovalCount);
        Assert.False(vm.HasPendingApproval);
    }

    /// <summary>Only plugins at NeedsConsent count — Load/Disabled/incompatible rows do not inflate the badge.</summary>
    [Fact]
    public void PendingApprovalCount_CountsOnlyNeedsConsentRows()
    {
        var vm = new PluginManagerViewModel();
        vm.Plugins.Add(_Row("consent-a", PluginLoadDecision.NeedsConsent));
        vm.Plugins.Add(_Row("consent-b", PluginLoadDecision.NeedsConsent));
        vm.Plugins.Add(_Row("loaded", PluginLoadDecision.Load));
        vm.Plugins.Add(_Row("disabled", PluginLoadDecision.Disabled));

        Assert.Equal(2, vm.PendingApprovalCount);
        Assert.True(vm.HasPendingApproval);
    }

    /// <summary>The badge clears once every awaiting plugin has been dealt with — the next LoadAsync repopulates
    /// Plugins with the fresh consent state, leaving nothing at NeedsConsent.</summary>
    [Fact]
    public void PendingApprovalCount_ClearsOnceThePluginIsNoLongerAwaitingConsent()
    {
        var vm = new PluginManagerViewModel();
        vm.Plugins.Add(_Row("consent", PluginLoadDecision.NeedsConsent));
        Assert.True(vm.HasPendingApproval);

        vm.Plugins.Clear();
        vm.Plugins.Add(_Row("consent", PluginLoadDecision.Load));

        Assert.Equal(0, vm.PendingApprovalCount);
        Assert.False(vm.HasPendingApproval);
    }

    /// <summary>Rebuilding Plugins (as LoadAsync does) raises change notifications for both the count and the badge,
    /// the same way AvailablePlugins does for the AC-76 update gate.</summary>
    [Fact]
    public void PluginsChanging_NotifiesPendingApprovalCountAndBadge()
    {
        var vm = new PluginManagerViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Plugins.Add(_Row("consent", PluginLoadDecision.NeedsConsent));

        Assert.Contains(nameof(PluginManagerViewModel.PendingApprovalCount), raised);
        Assert.Contains(nameof(PluginManagerViewModel.HasPendingApproval), raised);
    }

    /// <summary>
    /// The bug this was fixed for: at startup, Plugins is still empty — LoadAsync only runs once the operator
    /// opens the store — so counting off Plugins alone left the sidebar badge invisible until then. Seeding from
    /// PluginDiagnostics.PendingApprovals (via SeedPendingApprovalCount, called from CockpitViewModel's
    /// RefreshPluginFailures at the same startup point as the banner) must show the badge immediately instead.
    /// </summary>
    [Fact]
    public void SeedPendingApprovalCount_ShowsTheBadgeBeforePluginsIsEverPopulated()
    {
        var vm = new PluginManagerViewModel();

        vm.SeedPendingApprovalCount(2);

        Assert.Empty(vm.Plugins);
        Assert.Equal(2, vm.PendingApprovalCount);
        Assert.True(vm.HasPendingApproval);
    }

    /// <summary>The seed is only a snapshot from startup; once a real discovery finds nothing left at
    /// NeedsConsent (everything got approved or disabled), the live Plugins count must win and drop the badge to
    /// 0 — a stale seed must never be able to keep it showing forever.</summary>
    [Fact]
    public void AfterRediscoveryFindsNothingPending_TheSeededBadgeClearsToZero()
    {
        var vm = new PluginManagerViewModel();
        vm.SeedPendingApprovalCount(2);

        // Stands in for LoadAsync repopulating Plugins after the operator approved everything.
        vm.Plugins.Add(_Row("now-approved", PluginLoadDecision.Load));

        Assert.Equal(0, vm.PendingApprovalCount);
        Assert.False(vm.HasPendingApproval);
    }

    /// <summary>Once a real discovery has run, the seed can no longer move the badge — the live count owns it.</summary>
    [Fact]
    public void SeedPendingApprovalCount_IsANoOpOnceARealDiscoveryHasRun()
    {
        var vm = new PluginManagerViewModel();
        vm.Plugins.Add(_Row("consent", PluginLoadDecision.NeedsConsent));

        vm.SeedPendingApprovalCount(99);

        Assert.Equal(1, vm.PendingApprovalCount);
    }

    private static PluginRowViewModel _Row(string id, PluginLoadDecision decision) => new(new DiscoveredPlugin(
        $"/plugins/{id}", id,
        new PluginManifest(id, id, "1.0", $"{id}.dll", AbstractionsVersion: 1, EntryType: null, MinHostVersion: null, Description: null, Author: null),
        Sha256: "hash", decision));
}
