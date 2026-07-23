using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The sidebar "Plugin store" update badge (AC-76): its count drives whether the badge shows, and a change notifies both.</summary>
public class PluginManagerViewModelBadgeTests
{
    [Fact]
    public void HasUpdateBadge_ReflectsTheCount()
    {
        var vm = new PluginManagerViewModel();
        vm.HasUpdateBadge.Should().BeFalse();

        vm.UpdateBadgeCount = 3;
        vm.HasUpdateBadge.Should().BeTrue();

        vm.UpdateBadgeCount = 0;
        vm.HasUpdateBadge.Should().BeFalse();
    }

    [Fact]
    public void UpdateBadgeCount_Change_NotifiesCountAndBadge()
    {
        var vm = new PluginManagerViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.UpdateBadgeCount = 2;

        raised.Should().Contain(nameof(PluginManagerViewModel.UpdateBadgeCount));
        raised.Should().Contain(nameof(PluginManagerViewModel.HasUpdateBadge));
    }

    /// <summary>The AC-208 pending-approval badge: no rows at all, so no plugin can be awaiting approval.</summary>
    [Fact]
    public void NoPlugins_HasPendingApprovalIsFalse()
    {
        var vm = new PluginManagerViewModel();

        vm.PendingApprovalCount.Should().Be(0);
        vm.HasPendingApproval.Should().BeFalse();
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

        vm.PendingApprovalCount.Should().Be(2);
        vm.HasPendingApproval.Should().BeTrue();
    }

    /// <summary>The badge clears once every awaiting plugin has been dealt with — the next LoadAsync repopulates
    /// Plugins with the fresh consent state, leaving nothing at NeedsConsent.</summary>
    [Fact]
    public void PendingApprovalCount_ClearsOnceThePluginIsNoLongerAwaitingConsent()
    {
        var vm = new PluginManagerViewModel();
        vm.Plugins.Add(_Row("consent", PluginLoadDecision.NeedsConsent));
        vm.HasPendingApproval.Should().BeTrue();

        vm.Plugins.Clear();
        vm.Plugins.Add(_Row("consent", PluginLoadDecision.Load));

        vm.PendingApprovalCount.Should().Be(0);
        vm.HasPendingApproval.Should().BeFalse();
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

        raised.Should().Contain(nameof(PluginManagerViewModel.PendingApprovalCount));
        raised.Should().Contain(nameof(PluginManagerViewModel.HasPendingApproval));
    }

    private static PluginRowViewModel _Row(string id, PluginLoadDecision decision) => new(new DiscoveredPlugin(
        $"/plugins/{id}", id,
        new PluginManifest(id, id, "1.0", $"{id}.dll", AbstractionsVersion: 1, EntryType: null, MinHostVersion: null, Description: null, Author: null),
        Sha256: "hash", decision));
}
