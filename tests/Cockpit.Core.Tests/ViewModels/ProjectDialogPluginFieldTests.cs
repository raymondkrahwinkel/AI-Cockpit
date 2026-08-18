using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The fields plugins contribute to the project editor (AC-317). What is picked and what is stored are two
/// different strings — the operator reads "AI-Cockpit — AC" and the plugin queries with "AC" — so every test here
/// is about the pair staying honest: through a load, through typing, and through a save that must not drop a link
/// belonging to a plugin this machine does not have.
/// </summary>
public class ProjectDialogPluginFieldTests
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

    private static ProjectFieldRegistration YouTrackField(params ProjectFieldOption[] options) =>
        new("youtrack.project", "YouTrack project", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>(options));

    private static ProjectFieldRegistration FailingField(string message) =>
        new("youtrack.project", "YouTrack project", _ => throw new InvalidOperationException(message));

    private static Project LinkedProject(params (string Key, string Value)[] links) =>
        Project.Create("Cockpit") with
        {
            PluginFields = links.ToDictionary(link => link.Key, link => link.Value, StringComparer.Ordinal),
        };

    [Fact]
    public async Task CreateAsync_NoContributedFields_LeavesTheSectionOut()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        Assert.Empty(viewModel.PluginFields);
        Assert.False(viewModel.HasPluginFields);
    }

    [Fact]
    public async Task CreateAsync_ALinkedProject_ShowsTheIdentifierBeforeTheOptionsArrive()
    {
        // A box left blank while a network call runs reads as "not linked", and an operator who saves in that moment
        // makes it true. So the stored identifier is shown at once and only replaced by its nicer name once one is known.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")), ProfileStore(), Catalog(), [YouTrackField()]);

        var field = Assert.Single(viewModel.PluginFields);
        Assert.Equal("AC", field.Value);
        Assert.Equal("AC", field.Rows.Single().Text);
    }

    [Fact]
    public async Task LoadOptions_ASavedLink_IsShownUnderItsDisplayName()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")),
            ProfileStore(),
            Catalog(),
            [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"), new ProjectFieldOption("DEP", "Depot — DEP"))]);

        await viewModel.LoadPluginFieldOptionsAsync();

        var field = viewModel.PluginFields.Single();
        Assert.Equal("AI-Cockpit — AC", field.Rows.Single().Text);
        Assert.Equal("AC", field.Value);
    }

    [Fact]
    public async Task LoadOptions_AFetchThatFailed_SaysSoInsteadOfShowingAnEmptyList()
    {
        // "No options" and "the fetch failed" mean different things to someone deciding whether their project points
        // at the right place, and only one of them is the operator's to fix.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [FailingField("The token may not read projects.")]);

        await viewModel.LoadPluginFieldOptionsAsync();

        var field = viewModel.PluginFields.Single();
        Assert.Equal("The token may not read projects.", field.LoadError);
        Assert.False(field.IsLoadingOptions, "a bar still moving after a failure says the thing is still coming");
    }

    [Fact]
    public async Task TypingAnOptionsDisplayName_StoresThatOptionsIdentifier()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        await viewModel.LoadPluginFieldOptionsAsync();

        viewModel.PluginFields.Single().Rows.Single().Text = "AI-Cockpit — AC";

        Assert.Equal("AC", viewModel.PluginFields.Single().Value);
    }

    [Fact]
    public async Task AddingARow_AndPickingASecondOption_LinksBoth()
    {
        // AC-884: a second identifier gets its own row rather than a second value crammed into the first row's
        // text — an AutoCompleteBox already owns its own text on a pick, so sharing one across two identifiers is
        // what corrupted it (AC-884 bug).
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "EWB")),
            ProfileStore(),
            Catalog(),
            [YouTrackField(new ProjectFieldOption("AT", "EVE Workbench — AT"))]);
        await viewModel.LoadPluginFieldOptionsAsync();

        var field = viewModel.PluginFields.Single();
        field.AddRowCommand.Execute(null);
        field.Rows[1].Text = "EVE Workbench — AT";

        Assert.Equal("EWB, AT", field.Value);
        Assert.Equal(["EWB", "AT"], viewModel.ToProject().LinkedAsAll("youtrack.project"));
    }

    [Fact]
    public async Task RemovingARow_DropsItsIdentifier()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "EWB, AT")), ProfileStore(), Catalog(), [YouTrackField()]);

        var field = viewModel.PluginFields.Single();
        Assert.Equal(2, field.Rows.Count);
        field.RemoveRowCommand.Execute(field.Rows[1]);

        Assert.Equal(["EWB"], viewModel.ToProject().LinkedAsAll("youtrack.project"));
    }

    [Fact]
    public async Task RemovingTheFirstRow_IsANoOp()
    {
        // Raymond, AC-884 review: the first row is how a single identifier reads as unlinked once blanked — there
        // is nothing to fall back to it removing, so a call naming it changes nothing rather than emptying and
        // re-adding it.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")), ProfileStore(), Catalog(), [YouTrackField()]);

        var field = viewModel.PluginFields.Single();
        field.RemoveRowCommand.Execute(field.Rows.Single());

        Assert.Single(field.Rows);
        Assert.Equal("AC", field.Value);
    }

    [Fact]
    public async Task CanRemove_IsFalseOnlyOnTheFirstRow()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "EWB, AT")), ProfileStore(), Catalog(), [YouTrackField()]);

        var field = viewModel.PluginFields.Single();
        Assert.False(field.Rows[0].CanRemove);
        Assert.True(field.Rows[1].CanRemove);

        field.RemoveRowCommand.Execute(field.Rows[1]);

        Assert.False(field.Rows.Single().CanRemove);
    }

    [Fact]
    public async Task TypingSomethingTheSourceNeverReturned_IsKeptAsTyped()
    {
        // A repository nobody granted read access to is not in the list, and refusing it would be refusing the only
        // way to link it.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        await viewModel.LoadPluginFieldOptionsAsync();

        viewModel.PluginFields.Single().Rows.Single().Text = "PRIVATE";

        Assert.Equal("PRIVATE", viewModel.PluginFields.Single().Value);
    }

    [Fact]
    public async Task ToProject_AFilledField_BecomesTheProjectsLink()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        viewModel.Name = "Cockpit";
        await viewModel.LoadPluginFieldOptionsAsync();
        viewModel.PluginFields.Single().Rows.Single().Text = "AI-Cockpit — AC";

        Assert.Equal("AC", viewModel.ToProject().LinkedAs("youtrack.project"));
    }

    [Fact]
    public async Task ToProject_AFieldTheOperatorCleared_UnlinksTheProject()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")), ProfileStore(), Catalog(), [YouTrackField()]);

        viewModel.PluginFields.Single().Rows.Single().Text = string.Empty;

        Assert.Empty(viewModel.ToProject().PluginFields);
    }

    [Fact]
    public async Task ToProject_ALinkNoInstalledPluginRegistered_IsCarriedThrough()
    {
        // The Depot plugin is not installed on this machine. Editing the project's name must not quietly unlink it —
        // the same reason a disabled server name with no checklist row is kept.
        var project = LinkedProject(("youtrack.project", "AC"), ("depot.project", "ai-cockpit"));

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), [YouTrackField()]);
        viewModel.Name = "Renamed";

        Assert.Equal("ai-cockpit", viewModel.ToProject().LinkedAs("depot.project"));
    }

    [Fact]
    public async Task ToProject_AValueTypedWithSurroundingSpace_IsStoredTrimmed()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField()]);
        viewModel.Name = "Cockpit";

        viewModel.PluginFields.Single().Rows.Single().Text = "  AC  ";

        Assert.Equal("AC", viewModel.ToProject().LinkedAs("youtrack.project"));
    }
}
