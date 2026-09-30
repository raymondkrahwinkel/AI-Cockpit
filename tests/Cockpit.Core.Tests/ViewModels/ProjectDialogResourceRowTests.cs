using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The resource section's own row mechanics (AC-485): adding and removing a row, a blank one costing nothing on
/// save, a project with none behaving exactly as before it existed, and the two diagnostics the editor now runs for
/// itself — a reference the probe cannot find, and one that names a place only this machine has. Covers the five
/// acceptance criteria together with <see cref="ProjectDialogViewModelTests"/> (round trips, editing one row) and
/// <see cref="ProjectResourcePathPortabilityTests"/> (what a picked path is actually stored as).
/// </summary>
public class ProjectDialogResourceRowTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        return store;
    }

    private static IMcpServerCatalog Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns([]);
        return catalog;
    }

    // --- AC: add/remove a row, a blank one is not saved -------------------------------------------------------------

    // --- AC: a project with no resources behaves exactly as before (regression) --------------------------------------

    // --- AC: three roles round-trip unchanged, editing one row touches only that row -----------------------------------

    // --- AC: a broken reference is visible in the editor itself, not only in a prompt -----------------------------------

    // --- AC: a role switch cannot silently change what Reference means (AC-485 review, MUST-FIX 1) -----------------

    // --- AC-605: a resource row's scope is visible in the editor, and an in-folder absolute path gets a fix ------

    // --- AC-486: "Send along" is Instructions-only, off by default, round-trips, and cannot survive a role switch ----

    [Fact]
    public async Task RoundTrip_AnInstructionsRowWithSendsContentTicked_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook", SendsContent = true }],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        // CreateAsync must read the stored opt-in back onto the row.
        Assert.True(viewModel.ResourceRows.Single().SendsContent);
        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    [Fact]
    public async Task RoundTrip_AnInstructionsRowWithSendsContentLeftOff_SurvivesOpenAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("docs:handbook", ProjectResourceRole.Instructions) { Label = "Handbook" }],
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        Assert.False(viewModel.ResourceRows.Single().SendsContent);
        Assert.Equal(project.Resources, viewModel.ToProject().Resources);
    }

    // --- AC-503: a Memory row's own reachability check ---------------------------------------------------------

    // --- AC-503 acceptance criterion 6, the two reset sub-cases the review round asked to see proven directly ------
    // (rather than only inferred from OnRoleChanged/OnSelectedMemorySourceChoiceChanged unconditionally calling
    // _ResetReachability): a role switch away from Memory and back, and a source switch that leaves Reference's own
    // text untouched. Built directly against ProjectResourceRowViewModel rather than through CreateAsync, since
    // what is under test here is the row's own reset behavior, not the dialog's diagnostics pipeline.

    // --- AC-499: the second (family instance) axis — role switches, resets, and CanBrowse/IsMemoryFolderMode -------
    // Built directly against ProjectResourceRowViewModel's own constructor, the same way the two AC-503 reset tests
    // above are, since what is under test here is the row's own behaviour, not CreateAsync's building of it
    // (ProjectDialogMemorySourceFamilyTests covers that half).

}
