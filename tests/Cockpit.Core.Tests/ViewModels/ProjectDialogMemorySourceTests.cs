using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The "Memory" row's picker (AC-165/166): "Folder" plus one entry per contributed source. What is picked and what
/// is stored are different strings too, exactly the shape <see cref="ProjectDialogPluginFieldTests"/> already covers
/// for a plugin field — the operator sees "cockpit", the project stores "depot:cockpit" — with the same worry about
/// a plugin that is not installed: it must not lose or garble a reference just because this dialog was opened.
/// </summary>
public class ProjectDialogMemorySourceTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("personal", new ClaudeConfig("~/.claude"))]);
        return store;
    }

    private static IMcpServerCatalog Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns([]);
        return catalog;
    }

    private static ProjectMemorySourceRegistration DepotSource() =>
        new("depot", "Depot project", "Read it through the Depot MCP.");

    [Fact]
    public async Task CreateAsync_NoRegisteredSources_LeavesThePickerOutAndBehavesAsBefore()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "/home/raymond/notes" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.HasMemorySources.Should().BeFalse();
        viewModel.MemorySourceChoices.Should().BeEmpty();
        viewModel.SelectedMemorySourceChoice.Should().BeNull();
        viewModel.IsMemoryFolderMode.Should().BeTrue();
        viewModel.MemoryRef.Should().Be("/home/raymond/notes");
        viewModel.ToProject().MemoryRef.Should().Be("/home/raymond/notes");
    }

    [Fact]
    public async Task CreateAsync_SourcesRegistered_OffersFolderFirstThenEachSource()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        viewModel.HasMemorySources.Should().BeTrue();
        viewModel.MemorySourceChoices.Select(choice => choice.Label).Should().Equal("Folder", "Depot project");
        viewModel.MemorySourceChoices[0].Scheme.Should().BeNull();
        viewModel.MemorySourceChoices[1].Scheme.Should().Be("depot");

        // A new project has no stored MemoryRef to match against a source, so the picker must not render empty —
        // it defaults to the Folder choice, the same one it would show with no sources registered at all.
        viewModel.SelectedMemorySourceChoice.Should().Be(viewModel.MemorySourceChoices[0]);
    }

    [Fact]
    public async Task CreateAsync_TwoSourcesRegistered_OffersFolderThenBothInRegistrationOrder()
    {
        // One source alone cannot distinguish "add Folder once, up front" from "add Folder before every source" —
        // both produce the same two-item list. A second source tells them apart: the buggy shape would read
        // Folder, Depot project, Folder, Notes vault.
        var notes = new ProjectMemorySourceRegistration("notes", "Notes vault", "Read it there.");

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource(), notes]);

        viewModel.MemorySourceChoices.Select(choice => choice.Label).Should().Equal("Folder", "Depot project", "Notes vault");
        viewModel.MemorySourceChoices.Select(choice => choice.Scheme).Should().Equal(null, "depot", "notes");
    }

    [Fact]
    public async Task CreateAsync_AProjectPointingAtARegisteredScheme_SelectsThatSourceAndShowsTheBareValue()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        viewModel.SelectedMemorySourceChoice.Should().NotBeNull();
        viewModel.SelectedMemorySourceChoice!.Scheme.Should().Be("depot");
        viewModel.MemoryRef.Should().Be("cockpit", "the box shows what the plugin queries with, not the scheme prefix");
        viewModel.IsMemoryFolderMode.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_AProjectPointingAtAPath_SelectsFolderAndShowsThePath()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "/home/raymond/notes" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        viewModel.IsMemoryFolderMode.Should().BeTrue();
        // The picker must render "Folder" selected, not empty — a null selection here would leave the ComboBox
        // showing nothing for the everyday case of a project that simply points at a folder.
        viewModel.SelectedMemorySourceChoice.Should().Be(viewModel.MemorySourceChoices[0]);
        viewModel.MemoryRef.Should().Be("/home/raymond/notes");
    }

    /// <summary>
    /// The Depot plugin is not installed on this machine. Opening and saving the editor for an unrelated reason
    /// (renaming the project, say) must not lose or corrupt what it already pointed at.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AnUninstalledSchemesReference_SelectsFolderAndKeepsTheRawTextUntouchedOnSave()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        // No memorySources passed at all here — as if the Depot plugin were not installed.
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.IsMemoryFolderMode.Should().BeTrue();
        viewModel.MemoryRef.Should().Be("depot:cockpit", "with no source registered the raw reference is shown verbatim");

        viewModel.ToProject().MemoryRef.Should().Be("depot:cockpit", "saving without touching the field must not change it");
    }

    [Fact]
    public async Task ToProject_ASourceSelectedWithAValue_PrependsItsScheme()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");
        viewModel.MemoryRef = "cockpit";

        viewModel.ToProject().MemoryRef.Should().Be("depot:cockpit");
    }

    [Fact]
    public async Task ToProject_ASourceSelectedWithABlankValue_SavesNoReferenceAtAll()
    {
        // Not "depot:" — a bare scheme prefix names a source and nothing in it, which is not a reference at all.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");
        viewModel.MemoryRef = "   ";

        viewModel.ToProject().MemoryRef.Should().BeNull();
    }

    [Fact]
    public async Task ToProject_FolderSelectedWithAPath_SavesThePathUnprefixed()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.MemoryRef = "/home/raymond/notes";

        viewModel.ToProject().MemoryRef.Should().Be("/home/raymond/notes");
    }

    [Fact]
    public async Task RoundTrip_ADepotReference_SurvivesLoadAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        // Both checked so this is not merely "the string happens to match": the picker must actually have selected
        // the Depot source (not fallen back to Folder with the raw text carried through, which would print the same
        // saved string by coincidence).
        viewModel.SelectedMemorySourceChoice?.Scheme.Should().Be("depot");
        viewModel.ToProject().MemoryRef.Should().Be("depot:cockpit");
    }
}
