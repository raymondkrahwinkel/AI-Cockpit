using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The project editor (AC-160): what it opens with, and what it hands back on Save. The overlay it produces is
/// what a project's sessions actually get, so an unticked row that fails to reach the saved project is a server
/// silently still on.
/// </summary>
public class ProjectDialogViewModelTests
{
    private static ISessionProfileStore ProfileStore(params string[] labels)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            labels.Select(label => new SessionProfile(label, new ClaudeConfig("~/.claude"))).ToList());
        return store;
    }

    private static IMcpServerCatalog Catalog(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(servers);
        return catalog;
    }

    private static McpServerConfig Server(string name) => new() { Name = name, Command = "npx" };

    [Fact]
    public async Task CreateAsync_NewProject_OpensEmptyWithEveryServerTicked()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore("personal"), Catalog(Server("youtrack"), Server("depot")));

        viewModel.IsEditing.Should().BeFalse();
        viewModel.DialogTitle.Should().Be("New project");
        viewModel.CanSave.Should().BeFalse("a project needs a name");
        viewModel.McpServers.Should().OnlyContain(server => server.IsEnabledForSession);
    }

    [Fact]
    public async Task CreateAsync_ADisabledRegistryServer_IsNotOffered()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null,
            ProfileStore("personal"),
            Catalog(Server("depot"), new McpServerConfig { Name = "off", Enabled = false }));

        // A server switched off in the registry reaches no session at all, so offering it here as a per-project
        // toggle would promise a project something it cannot have — and every other picker already leaves it out.
        viewModel.McpServers.Select(server => server.Name).Should().Equal("depot");
    }

    [Fact]
    public async Task ToProject_KeepsADisabledNameTheChecklistCannotShow()
    {
        // The project switched "gone" off while it existed; it has since left the registry. Saving must not read the
        // missing row as "switched back on" — that would quietly re-enable a server the operator had turned off.
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["gone"] },
        };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog(Server("depot")));

        viewModel.ToProject().McpOverlay.DisabledServerNames.Should().Contain("gone");
    }

    [Fact]
    public async Task CreateAsync_InternalServers_AreNotOffered()
    {
        var internalServer = new McpServerConfig { Name = "autopilot-ceo", Internal = true };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), internalServer));

        viewModel.McpServers.Select(server => server.Name).Should().Equal("youtrack");
    }

    [Fact]
    public async Task CreateAsync_ExistingProject_UnticksTheServersItTurnedOff()
    {
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["depot"] },
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(Server("youtrack"), Server("depot")));

        viewModel.IsEditing.Should().BeTrue();
        viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession.Should().BeFalse();
        viewModel.McpServers.Single(server => server.Name == "youtrack").IsEnabledForSession.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_ExistingProject_PreselectsItsProfileByLabel()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal", "work"), Catalog());

        viewModel.SelectedProfileLabel.Should().Be("work");
    }

    /// <summary>A label whose profile was since renamed or removed must not be handed back as a selection nothing resolves.</summary>
    [Fact]
    public async Task CreateAsync_ProfileLabelThatNoLongerExists_LeavesTheSelectionEmpty()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "deleted" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore("personal"), Catalog());

        viewModel.SelectedProfileLabel.Should().BeNull();
    }

    [Fact]
    public async Task ToProject_UntickedServers_BecomeTheOverlaysDisabledList()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(Server("youtrack"), Server("depot")));
        viewModel.Name = "Cockpit";
        viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession = false;

        viewModel.ToProject().McpOverlay.DisabledServerNames.Should().Equal("depot");
    }

    [Fact]
    public async Task ToProject_Editing_KeepsTheIdSoReferencesStillResolve()
    {
        var project = Project.Create("Cockpit");

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.ToProject().Id.Should().Be(project.Id);
    }

    /// <summary>v1 edits which servers are on, not the servers themselves — a project's own servers must survive an edit.</summary>
    [Fact]
    public async Task ToProject_Editing_CarriesTheProjectsOwnServersThrough()
    {
        var project = Project.Create("Cockpit") with
        {
            McpOverlay = new ProjectMcpOverlay { AdditionalServers = [Server("project-tools")] },
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.ToProject().McpOverlay.AdditionalServers.Should().ContainSingle()
            .Which.Name.Should().Be("project-tools");
    }

    /// <summary>Likewise for what v2 writes: editing a project in v1 must not drop its knowledge-store reference (AC-166).</summary>
    [Fact]
    public async Task ToProject_Editing_CarriesTheMemoryReferenceThrough()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:ai-cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.ToProject().MemoryRef.Should().Be("depot:ai-cockpit");
    }

    [Fact]
    public async Task ToProject_BlankOptionalFields_AreStoredAsAbsentRatherThanEmpty()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "  Cockpit  ";
        viewModel.Description = "   ";
        viewModel.BehaviorPrompt = string.Empty;

        var project = viewModel.ToProject();

        project.Name.Should().Be("Cockpit");
        project.Description.Should().BeNull();
        project.BehaviorPrompt.Should().BeNull();
    }

    [Fact]
    public async Task ApplyPickedDirectory_FromAClone_KeepsTheUrlBesideThePath()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        viewModel.Name = "Cockpit";

        viewModel.ApplyPickedDirectory("/home/raymond/clones/cockpit", "https://example.test/cockpit.git");

        var project = viewModel.ToProject();
        project.SourceDirectory.Should().Be("/home/raymond/clones/cockpit");
        project.GitUrl.Should().Be("https://example.test/cockpit.git");
    }

    /// <summary>Pointing an existing project at a folder of its own drops the clone URL, which no longer describes where it came from.</summary>
    [Fact]
    public async Task ApplyPickedDirectory_WithoutAUrl_ClearsAStaleCloneUrl()
    {
        var project = Project.Create("Cockpit") with { GitUrl = "https://example.test/old.git" };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        viewModel.ApplyPickedDirectory("/home/raymond/elsewhere");

        viewModel.ToProject().GitUrl.Should().BeNull();
    }

    [Fact]
    public async Task SaveCommand_BecomesAvailableOnlyOnceTheProjectHasAName()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        viewModel.Name = "Cockpit";
        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CancelCommand_ClosesWithoutAProject()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog());
        Project? closedWith = Project.Create("sentinel");
        var closed = false;
        viewModel.CloseRequested += project =>
        {
            closedWith = project;
            closed = true;
        };

        viewModel.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        closedWith.Should().BeNull();
    }
}
