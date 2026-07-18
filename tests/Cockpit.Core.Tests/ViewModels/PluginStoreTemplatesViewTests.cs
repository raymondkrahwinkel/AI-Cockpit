using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The workflow templates have a scope of their own in the store (#69). They came from the same stores and the same
/// index — that part is right — but they were shown as a section under the plugin grid, which is a place nobody
/// scrolls to. They are also not plugins: nothing is loaded and no code runs, and what you agree to is the steps that
/// land on your canvas. A different kind of thing gets its own place to be looked at.
/// </summary>
public class PluginStoreTemplatesViewTests
{
    [Fact]
    public void TheSidebar_OffersTheTemplates_AndSaysHowMany()
    {
        var manager = new PluginManagerViewModel();
        manager.AvailableTemplates.Add(_Template("cockpit.morning-briefing", "Morning briefing"));
        manager.AvailableTemplates.Add(_Template("cockpit.nightly-tests", "Nightly tests"));

        var dialog = new PluginStoreDialogViewModel(manager);

        dialog.SidebarItems.Should().Contain(item => item.Label == "Workflow templates (2)" && item.IsEnabled);
    }

    // A store that offers none still lists the entry, greyed out — the same rule the Installed/Updates entries follow.
    // Vanishing entries make a sidebar that changes shape between loads.
    [Fact]
    public void WithNoTemplatesOnOffer_TheEntryIsThereButUnclickable()
    {
        var dialog = new PluginStoreDialogViewModel(new PluginManagerViewModel());

        dialog.SidebarItems.Should().Contain(item => item.Label == "Workflow templates (0)" && !item.IsEnabled);
    }

    // The templates arrive after the plugins do, and the sidebar was only counting them when the plugin list changed —
    // so it said "(0)", greyed out and unclickable, while the store was offering ten.
    [Fact]
    public void TemplatesArrivingAfterTheDialogOpened_MakeTheEntryClickable()
    {
        var manager = new PluginManagerViewModel();
        var dialog = new PluginStoreDialogViewModel(manager);

        manager.AvailableTemplates.Add(_Template("cockpit.morning-briefing", "Morning briefing"));

        dialog.SidebarItems.Should().Contain(item => item.Label == "Workflow templates (1)" && item.IsEnabled);
    }

    [Fact]
    public void SelectingIt_ShowsTheTemplates_AndNotThePluginCatalogue()
    {
        var manager = new PluginManagerViewModel();
        manager.AvailableTemplates.Add(_Template("cockpit.morning-briefing", "Morning briefing"));
        var dialog = new PluginStoreDialogViewModel(manager);

        dialog.SelectedSidebarItem = dialog.SidebarItems.Single(item => item.Filter == PluginStoreFilter.Templates);

        dialog.IsTemplatesView.Should().BeTrue();
        dialog.IsInstalledView.Should().BeFalse();
        dialog.FilteredTemplates.Should().ContainSingle();
    }

    // Whoever searches for "review" has that word in the description more often than in the name.
    [Fact]
    public void Searching_ReadsTheDescriptionToo_NotOnlyTheName()
    {
        var manager = new PluginManagerViewModel();
        manager.AvailableTemplates.Add(_Template("cockpit.delegate-review", "Delegate a review", "Hands your staged diff to a local model."));
        manager.AvailableTemplates.Add(_Template("cockpit.morning-briefing", "Morning briefing", "What happened since yesterday."));
        var dialog = new PluginStoreDialogViewModel(manager);

        dialog.SearchText = "diff";

        dialog.FilteredTemplates.Should().ContainSingle().Which.Id.Should().Be("cockpit.delegate-review");
    }

    private static StoreTemplateRowViewModel _Template(string id, string name, string description = "A flow.") =>
        new(
            new WorkflowTemplateStoreEntry(id, name, description, "Cockpit", "1.0", $"templates/{id}.json"),
            PluginStoreConfig.Remote("https://example.com/index.json"),
            isInstalled: false);
}
