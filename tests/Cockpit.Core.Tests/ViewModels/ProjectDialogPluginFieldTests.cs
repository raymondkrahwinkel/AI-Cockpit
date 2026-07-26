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

        viewModel.PluginFields.Should().BeEmpty();
        viewModel.HasPluginFields.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_ALinkedProject_ShowsTheIdentifierBeforeTheOptionsArrive()
    {
        // A box left blank while a network call runs reads as "not linked", and an operator who saves in that moment
        // makes it true. So the stored identifier is shown at once and only replaced by its nicer name once one is known.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")), ProfileStore(), Catalog(), [YouTrackField()]);

        var field = viewModel.PluginFields.Should().ContainSingle().Subject;
        field.Value.Should().Be("AC");
        field.Text.Should().Be("AC");
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
        field.Text.Should().Be("AI-Cockpit — AC");
        field.Value.Should().Be("AC", "showing the friendly name must not change what the plugin queries with");
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
        field.LoadError.Should().Be("The token may not read projects.");
        field.IsLoadingOptions.Should().BeFalse("a bar still moving after a failure says the thing is still coming");
    }

    [Fact]
    public async Task TypingAnOptionsDisplayName_StoresThatOptionsIdentifier()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        await viewModel.LoadPluginFieldOptionsAsync();

        viewModel.PluginFields.Single().Text = "AI-Cockpit — AC";

        viewModel.PluginFields.Single().Value.Should().Be("AC");
    }

    [Fact]
    public async Task TypingSomethingTheSourceNeverReturned_IsKeptAsTyped()
    {
        // A repository nobody granted read access to is not in the list, and refusing it would be refusing the only
        // way to link it.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        await viewModel.LoadPluginFieldOptionsAsync();

        viewModel.PluginFields.Single().Text = "PRIVATE";

        viewModel.PluginFields.Single().Value.Should().Be("PRIVATE");
    }

    [Fact]
    public async Task ToProject_AFilledField_BecomesTheProjectsLink()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField(new ProjectFieldOption("AC", "AI-Cockpit — AC"))]);
        viewModel.Name = "Cockpit";
        await viewModel.LoadPluginFieldOptionsAsync();
        viewModel.PluginFields.Single().Text = "AI-Cockpit — AC";

        viewModel.ToProject().LinkedAs("youtrack.project").Should().Be("AC");
    }

    [Fact]
    public async Task ToProject_AFieldTheOperatorCleared_UnlinksTheProject()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            LinkedProject(("youtrack.project", "AC")), ProfileStore(), Catalog(), [YouTrackField()]);

        viewModel.PluginFields.Single().Text = string.Empty;

        viewModel.ToProject().PluginFields.Should().BeEmpty("clearing the box is how a link is removed");
    }

    [Fact]
    public async Task ToProject_ALinkNoInstalledPluginRegistered_IsCarriedThrough()
    {
        // The Depot plugin is not installed on this machine. Editing the project's name must not quietly unlink it —
        // the same reason a disabled server name with no checklist row is kept.
        var project = LinkedProject(("youtrack.project", "AC"), ("depot.project", "ai-cockpit"));

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog(), [YouTrackField()]);
        viewModel.Name = "Renamed";

        viewModel.ToProject().LinkedAs("depot.project").Should().Be("ai-cockpit");
    }

    [Fact]
    public async Task ToProject_AValueTypedWithSurroundingSpace_IsStoredTrimmed()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), [YouTrackField()]);
        viewModel.Name = "Cockpit";

        viewModel.PluginFields.Single().Text = "  AC  ";

        viewModel.ToProject().LinkedAs("youtrack.project").Should().Be("AC");
    }
}
