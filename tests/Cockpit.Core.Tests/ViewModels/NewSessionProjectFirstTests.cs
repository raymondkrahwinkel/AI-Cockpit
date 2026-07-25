using FluentAssertions;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Starting a session under a project (AC-163): picking one pre-fills the dialog through the same precedence rule
/// every other surface uses, and the started session carries what the project decided. The rule is that the project
/// overrides and the profile falls back — a wrong answer here is a session working in the wrong folder or talking
/// to the wrong servers.
/// </summary>
public class NewSessionProjectFirstTests
{
    private static readonly SessionProfile Personal = new("personal", new ClaudeConfig("~/.claude-personal"));
    private static readonly SessionProfile Work = new("work", new ClaudeConfig("~/.claude-work"));

    private static NewSessionDialogViewModel Build(
        IReadOnlyList<Project> projects,
        IReadOnlyList<McpServerConfig>? registry = null,
        IReadOnlyList<McpServerConfig>? projectRegistry = null)
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([Personal, Work]);

        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var catalog = Substitute.For<IMcpServerCatalog>();
        var plain = registry ?? [];
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(plain.ToList());
        catalog.GetServersForProjectAsync(null, Arg.Any<CancellationToken>()).Returns(plain.ToList());
        catalog.GetServersForProjectAsync(Arg.Is<string?>(id => id != null), Arg.Any<CancellationToken>())
            .Returns((projectRegistry ?? plain).ToList());

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = projects });

        return new NewSessionDialogViewModel(profileStore, loginChecker, catalog, projectStore: projectStore);
    }

    [Fact]
    public async Task LoadAsync_NoProjects_HidesThePicker()
    {
        var viewModel = Build([]);

        await viewModel.LoadAsync();

        viewModel.HasProjects.Should().BeFalse("a cockpit without projects opens the dialog exactly as before");
        viewModel.SelectedProject.Should().BeNull();
    }

    [Fact]
    public async Task SelectingAProject_PreselectsItsProfile()
    {
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };
        var viewModel = Build([project]);
        await viewModel.LoadAsync();

        viewModel.SelectedProject = viewModel.Projects[0];

        viewModel.SelectedProfile?.Label.Should().Be("work");
    }

    [Fact]
    public async Task SelectingAProject_PrefillsItsFolder()
    {
        var project = Project.Create("Cockpit") with { SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit" };
        var viewModel = Build([project]);
        await viewModel.LoadAsync();

        viewModel.SelectedProject = viewModel.Projects[0];

        viewModel.WorkingDirectory.Should().Be("/home/raymond/RiderProjects/AI-Cockpit");
    }

    /// <summary>The project's folder overrides the profile's default — that ordering is the whole precedence rule.</summary>
    [Fact]
    public async Task SelectingAProject_ItsFolderWinsOverTheProfilesDefault()
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns([Personal with { DefaultWorkingDirectory = "/home/raymond/profile-dir" }]);

        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);

        var project = Project.Create("Cockpit") with { SourceDirectory = "/home/raymond/project-dir" };
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });

        var viewModel = new NewSessionDialogViewModel(profileStore, loginChecker, catalog, projectStore: projectStore);
        await viewModel.LoadAsync();

        viewModel.SelectedProject = viewModel.Projects[0];

        viewModel.WorkingDirectory.Should().Be("/home/raymond/project-dir");
    }

    /// <summary>
    /// Changing the profile after picking a project leaves the project's folder standing: the profile is the
    /// fallback, so a profile without a folder of its own has nothing to say here and must not blank the field.
    /// </summary>
    [Fact]
    public async Task SwitchingProfileUnderAProject_KeepsTheProjectsFolder()
    {
        var project = Project.Create("Cockpit") with { SourceDirectory = "/home/raymond/project-dir" };
        var viewModel = Build([project]);
        await viewModel.LoadAsync();

        viewModel.SelectedProject = viewModel.Projects[0];
        viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.Label == "work");

        viewModel.WorkingDirectory.Should().Be("/home/raymond/project-dir");
    }

    /// <summary>A folder the operator typed is theirs; picking a project must not overwrite it.</summary>
    [Fact]
    public async Task SelectingAProject_LeavesAFolderTheOperatorAlreadyTypedAlone()
    {
        var project = Project.Create("Cockpit") with { SourceDirectory = "/home/raymond/project-dir" };
        var viewModel = Build([project]);
        await viewModel.LoadAsync();

        viewModel.WorkingDirectory = "/home/raymond/somewhere-else";
        viewModel.SelectedProject = viewModel.Projects[0];

        viewModel.WorkingDirectory.Should().Be("/home/raymond/somewhere-else");
    }

    [Fact]
    public async Task SelectingAProject_RebuildsTheChecklistFromItsOverlay()
    {
        var project = Project.Create("Cockpit");
        var viewModel = Build(
            [project],
            registry: [new McpServerConfig { Name = "youtrack" }, new McpServerConfig { Name = "depot" }],
            projectRegistry: [new McpServerConfig { Name = "youtrack" }, new McpServerConfig { Name = "project-tools" }]);
        await viewModel.LoadAsync();

        viewModel.McpServers.Select(server => server.Name).Should().BeEquivalentTo(["youtrack", "depot"]);

        viewModel.SelectedProject = viewModel.Projects[0];
        await Task.Yield();

        viewModel.McpServers.Select(server => server.Name).Should().BeEquivalentTo(
            ["youtrack", "project-tools"],
            "the overlay decides which servers exist for this project's sessions, which a tick cannot express");
    }

    [Fact]
    public async Task Confirm_CarriesTheProjectIdSoTheSessionCanResolveItsOverlay()
    {
        var project = Project.Create("Cockpit");
        var viewModel = Build([project]);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        NewSessionResult? result = null;
        viewModel.CloseRequested += value => result = value;
        viewModel.ConfirmCommand.Execute(null);

        result?.ProjectId.Should().Be(project.Id);
    }

    [Fact]
    public async Task SelectingAProject_KeepsTheServersTheOperatorHadAlreadyUnticked()
    {
        // The rebuild hands out fresh rows, which start ticked, and an operator who has edited the checklist is
        // deliberately not re-served the profile's saved selection. Together that turned one manual untick plus a
        // project switch into every server back on — the opposite of both intents.
        var project = Project.Create("Cockpit");
        var viewModel = Build([project], registry: [new McpServerConfig { Name = "depot" }, new McpServerConfig { Name = "youtrack" }]);
        await viewModel.LoadAsync();
        viewModel.McpServers.Single(server => server.Name == "youtrack").IsEnabledForSession = false;

        viewModel.SelectedProject = viewModel.Projects[0];
        await viewModel.McpChecklistRefresh;

        viewModel.McpServers.Single(server => server.Name == "youtrack").IsEnabledForSession.Should().BeFalse();
        viewModel.McpServers.Single(server => server.Name == "depot").IsEnabledForSession.Should().BeTrue();
    }

    [Fact]
    public async Task WhileTheChecklistIsBeingRebuilt_StartIsHeldBack()
    {
        // Switching project rebuilds the checklist off disk, and Start reads that checklist. Pressed inside that
        // window it would carry the previous project's ticks into the new project's session — the folder and the id
        // from one, the servers from the other. The catalog is held open here so the window is observable at all:
        // a substitute that answers instantly closes it before a test (or an operator) can look.
        var project = Project.Create("Cockpit");
        var pending = new TaskCompletionSource<IReadOnlyList<McpServerConfig>>();

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([Personal]);
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(null, Arg.Any<CancellationToken>()).Returns([]);
        catalog.GetServersForProjectAsync(Arg.Is<string?>(id => id != null), Arg.Any<CancellationToken>())
            .Returns(_ => pending.Task);

        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });

        var viewModel = new NewSessionDialogViewModel(profileStore, loginChecker, catalog, projectStore: projectStore);
        await viewModel.LoadAsync();
        viewModel.CanStart.Should().BeTrue("nothing is in flight yet");

        viewModel.SelectedProject = viewModel.Projects[0];

        viewModel.CanStart.Should().BeFalse("the rebuild is in flight and Start would read the old checklist");

        pending.SetResult([]);
        await viewModel.McpChecklistRefresh;

        viewModel.CanStart.Should().BeTrue("and released once it landed");
    }

    [Fact]
    public async Task Confirm_UnderAProjectThatOffersNoServers_SelectsNothingRatherThanLeavingItOpen()
    {
        // The registry has a server; this project's overlay leaves none. A null selection would read downstream as
        // "no choice made" and fall back to the profile's list over the unscoped registry — mounting the very server
        // the project switched off.
        var project = Project.Create("Cockpit");
        var viewModel = Build([project], registry: [new McpServerConfig { Name = "depot" }], projectRegistry: []);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        NewSessionResult? result = null;
        viewModel.CloseRequested += value => result = value;
        viewModel.ConfirmCommand.Execute(null);

        result?.EnabledMcpServerNames.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>The AC-142 half: the profile's identity and the project's behaviour reach the launch as one appended prompt.</summary>
    [Fact]
    public async Task Confirm_CarriesTheProfileIdentityWithTheProjectsBehaviourUnderIt()
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns([Personal with { SystemPrompt = "You are Olaf. Your memory is in the Depot MCP." }]);

        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);

        var project = Project.Create("Cockpit") with { BehaviorPrompt = "Test before opening a PR." };
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });

        var viewModel = new NewSessionDialogViewModel(profileStore, loginChecker, catalog, projectStore: projectStore);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        NewSessionResult? result = null;
        viewModel.CloseRequested += value => result = value;
        viewModel.ConfirmCommand.Execute(null);

        result?.SystemPrompt.Should().Be("You are Olaf. Your memory is in the Depot MCP.\n\nTest before opening a PR.");
    }

    [Fact]
    public async Task Confirm_NoProject_CarriesNeitherIdNorInstructions()
    {
        var viewModel = Build([Project.Create("Cockpit")]);
        await viewModel.LoadAsync();

        NewSessionResult? result = null;
        viewModel.CloseRequested += value => result = value;
        viewModel.ConfirmCommand.Execute(null);

        result?.ProjectId.Should().BeNull();
        result?.SystemPrompt.Should().BeNull();
    }
}
