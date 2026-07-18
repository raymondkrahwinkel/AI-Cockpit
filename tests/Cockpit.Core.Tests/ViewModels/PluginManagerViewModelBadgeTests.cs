using Cockpit.App.ViewModels;
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
}
