using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A Memory row's picker (AC-165/166): "Folder" plus one entry per contributed source. What is picked and what is
/// stored are different strings too, exactly the shape <see cref="ProjectDialogPluginFieldTests"/> already covers
/// for a plugin field — the operator sees "cockpit", the project stores "depot:cockpit" — with the same worry about
/// a plugin that is not installed: it must not lose or garble a reference just because this dialog was opened.
/// <para>
/// AC-485 moved this picker from a single dialog-wide field onto <see cref="ProjectResourceRowViewModel"/> itself —
/// <see cref="ProjectDialogViewModel.MemorySourceChoices"/> is still the one shared list of choices, but which one
/// is picked, and the reference typed beside it, now live on each row.
/// </para>
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

    /// <summary>
    /// AC-499 review: this test used to pin the pre-AC-499 behaviour — no source registered meant
    /// <see cref="ProjectDialogViewModel.MemorySourceChoices"/> was left empty and the picker disappeared entirely
    /// (<see cref="ProjectResourceRowViewModel.ShowsMemorySourcePicker"/> false). That was the doorless dead end
    /// Raymond reported: with nothing registered, a Memory row offered no way to say "this is a folder" as a
    /// deliberate choice — it simply had no picker at all. AC-499 makes "Folder" unconditional — offered from
    /// <c>CreateAsync</c> whether or not any plugin ever registers anything — so this now pins the corrected
    /// behaviour instead: the picker is always shown, with exactly "Folder" in it.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NoRegisteredSourcesOrFamilies_StillOffersFolderAndBehavesAsBefore()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "/home/raymond/notes" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.Equal(new[] { "Folder" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
        var row = Assert.Single(viewModel.ResourceRows);
        Assert.True(row.ShowsMemorySourcePicker);
        Assert.Equal(viewModel.MemorySourceChoices[0], row.SelectedMemorySourceChoice);
        Assert.True(row.IsMemoryFolderMode);
        Assert.Equal("/home/raymond/notes", row.Reference);
        Assert.Equal("/home/raymond/notes", viewModel.ToProject().MemoryRef);
    }

    [Fact]
    public async Task CreateAsync_SourcesRegistered_OffersFolderFirstThenEachSource()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        Assert.Equal(new[] { "Folder", "Depot project" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
        Assert.Null(viewModel.MemorySourceChoices[0].Scheme);
        Assert.Equal("depot", viewModel.MemorySourceChoices[1].Scheme);

        // A freshly added row has no stored reference to match against a source, so it must not render empty — it
        // defaults to the Folder choice, the same one it would show with no sources registered at all.
        viewModel.AddResourceRowCommand.Execute(null);
        Assert.Equal(viewModel.MemorySourceChoices[0], viewModel.ResourceRows.Single().SelectedMemorySourceChoice);
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

        Assert.Equal(new[] { "Folder", "Depot project", "Notes vault" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
        Assert.Equal(new string?[] { null, "depot", "notes" }, viewModel.MemorySourceChoices.Select(choice => choice.Scheme));
    }

    [Fact]
    public async Task CreateAsync_AProjectPointingAtARegisteredScheme_SelectsThatSourceAndShowsTheBareValue()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.NotNull(row.SelectedMemorySourceChoice);
        Assert.Equal("depot", row.SelectedMemorySourceChoice!.Scheme);
        Assert.Equal("cockpit", row.Reference);
        Assert.False(row.IsMemoryFolderMode);
    }

    [Fact]
    public async Task CreateAsync_AProjectPointingAtAPath_SelectsFolderAndShowsThePath()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "/home/raymond/notes" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.True(row.IsMemoryFolderMode);
        // The picker must render "Folder" selected, not empty — a null selection here would leave the ComboBox
        // showing nothing for the everyday case of a project that simply points at a folder.
        Assert.Equal(viewModel.MemorySourceChoices[0], row.SelectedMemorySourceChoice);
        Assert.Equal("/home/raymond/notes", row.Reference);
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

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.True(row.IsMemoryFolderMode);
        Assert.Equal("depot:cockpit", row.Reference);

        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    }

    [Fact]
    public async Task ToProject_ASourceSelectedWithAValue_PrependsItsScheme()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");
        row.Reference = "cockpit";

        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    }

    [Fact]
    public async Task ToProject_ASourceSelectedWithABlankValue_SavesNoReferenceAtAll()
    {
        // Not "depot:" — a bare scheme prefix names a source and nothing in it, which is not a reference at all.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");
        row.Reference = "   ";

        Assert.Null(viewModel.ToProject().MemoryRef);
    }

    [Fact]
    public async Task ToProject_FolderSelectedWithAPath_SavesThePathUnprefixed()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.ResourceRows.Single().Reference = "/home/raymond/notes";

        Assert.Equal("/home/raymond/notes", viewModel.ToProject().MemoryRef);
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
        Assert.Equal("depot", viewModel.ResourceRows.Single().SelectedMemorySourceChoice?.Scheme);
        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    }

    /// <summary>
    /// Caught by rendering the row, not by a test: with a source picked, the hint went on calling the location "a
    /// folder, kept apart from the source folder" while the box beside it held a project key — the one line on the
    /// row asserting exactly the thing this feature exists to stop assuming.
    /// </summary>
    [Fact]
    public async Task MemoryHint_ASourceSelected_StopsCallingTheLocationAFolder()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [DepotSource()]);
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();

        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");

        Assert.DoesNotContain("a folder", row.MemoryHint);
    }

    [Fact]
    public async Task MemoryHint_NoSourceRegistered_ReadsExactlyAsItDidBefore()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: []);
        viewModel.AddResourceRowCommand.Execute(null);

        Assert.Equal("Where this project's memory lives — a folder, kept apart from the source folder. Sessions are told " +
            "about it, so they can look things up instead of being told again.", viewModel.ResourceRows.Single().MemoryHint);
    }
}
