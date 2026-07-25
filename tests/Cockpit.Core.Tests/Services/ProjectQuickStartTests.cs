using FluentAssertions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// What a session started straight from a project opens with (AC-164) — the answers the New-session dialog would
/// have reached, without it being shown. Both the sidebar's ▶ and the launcher's Start come through here, so these
/// are the terms the whole quick-start path starts on.
/// </summary>
public class ProjectQuickStartTests
{
    private static readonly SessionProfile ClaudeProfile = new("work", new ClaudeConfig("/home/raymond/.claude"));

    private static readonly SessionProfile LocalProfile = new("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

    private static ProjectQuickStart Build(
        IReadOnlyList<SessionProfile> profiles,
        IReadOnlyList<McpServerConfig>? servers = null,
        ITtySessionProvider? ttyProvider = null)
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(servers ?? []);

        var ttyProviders = Substitute.For<ITtySessionProviderResolver>();
        ttyProviders.Resolve(Arg.Any<SessionProfile?>()).Returns(ttyProvider);

        return new ProjectQuickStart(profileStore, catalog, ttyProviders);
    }

    private static McpServerConfig Server(string name, bool enabled = true, bool @internal = false, bool alwaysMounted = false) =>
        new() { Name = name, Enabled = enabled, Internal = @internal, AlwaysMounted = alwaysMounted };

    [Fact]
    public async Task WithoutADefaultProfile_ComposesNothing()
    {
        var quickStart = Build([ClaudeProfile]);

        var result = await quickStart.ComposeAsync(Project.Create("Cockpit"));

        result.Should().BeNull("a session needs a profile to run on, and the project names none");
    }

    [Fact]
    public async Task WhenTheProjectsProfileIsGone_ComposesNothing()
    {
        var quickStart = Build([ClaudeProfile]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "removed" };

        var result = await quickStart.ComposeAsync(project);

        result.Should().BeNull();
    }

    [Fact]
    public async Task StartsOnTheProjectsFolderIsolationAndIdentity()
    {
        var profile = ClaudeProfile with { SystemPrompt = "You are Olaf." };
        var quickStart = Build([profile]);
        var project = Project.Create("Cockpit") with
        {
            DefaultProfileLabel = "work",
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            IsolateInWorktreeByDefault = true,
            BehaviorPrompt = "Work ticket by ticket.",
        };

        var result = await quickStart.ComposeAsync(project);

        result.Should().NotBeNull();
        result!.Profile.Should().BeSameAs(profile);
        result.SessionName.Should().Be("Cockpit", "the operator picked the project, so that is what the session is");
        result.WorkingDirectory.Should().Be("/home/raymond/RiderProjects/AI-Cockpit");
        result.IsolateInWorktree.Should().BeTrue();
        result.ProjectId.Should().Be(project.Id);
        result.SystemPrompt.Should().Be("You are Olaf.\n\nWork ticket by ticket.");
    }

    [Fact]
    public async Task TicksWhatTheProjectSaysAndNotWhatTheProfileSaved()
    {
        // The project's answer beats the profile's (Raymond, 2026-07-24): the profile here wants only depot, the
        // project switched off playwright, and what starts is everything except playwright.
        var profile = ClaudeProfile with { EnabledMcpServerNames = ["depot"] };
        var quickStart = Build(
            [profile],
            [Server("depot"), Server("youtrack"), Server("playwright")]);
        var project = Project.Create("Cockpit") with
        {
            DefaultProfileLabel = "work",
            McpOverlay = new ProjectMcpOverlay { DisabledServerNames = ["playwright"] },
        };

        var result = await quickStart.ComposeAsync(project);

        result!.EnabledMcpServerNames.Should().BeEquivalentTo("depot", "youtrack");
    }

    [Fact]
    public async Task AProjectThatSwitchedNothingOff_TicksEveryOfferedServer()
    {
        var quickStart = Build([ClaudeProfile], [Server("depot"), Server("youtrack")]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        result!.EnabledMcpServerNames.Should().BeEquivalentTo("depot", "youtrack");
    }

    [Fact]
    public async Task LeavesOutTheServersNoChecklistOffers()
    {
        var quickStart = Build(
            [ClaudeProfile],
            [Server("depot"), Server("off", enabled: false), Server("autopilot", @internal: true), Server("cockpit-session", alwaysMounted: true)]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        result!.EnabledMcpServerNames.Should().BeEquivalentTo("depot");
    }

    [Fact]
    public async Task ReadsTheCatalogAsTheProjectSeesIt()
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([ClaudeProfile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Server("depot")]);
        var quickStart = new ProjectQuickStart(profileStore, catalog, Substitute.For<ITtySessionProviderResolver>());
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        await quickStart.ComposeAsync(project);

        // Read as the project sees it: the plain catalog does not hold the servers a project brings of its own,
        // so asking that one would leave them off the checklist entirely.
        await catalog.Received(1).GetServersForProjectAsync(project.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNothingOnOffer_SelectsNothingRatherThanLeavingItOpen()
    {
        // A null selection reads downstream as "this launch chose nothing", answered by falling back to the
        // profile's saved list — which would quietly put the profile back in charge of a session started from a
        // project. An empty set says what is true: this one starts with no servers.
        var quickStart = Build([ClaudeProfile with { EnabledMcpServerNames = ["depot"] }]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        result!.EnabledMcpServerNames.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task AProfileWithATuiStartsOne()
    {
        var quickStart = Build([ClaudeProfile]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        result!.Kind.Should().Be(SessionKind.Tty);
        result.ReadingLevel.Should().BeNull("a reading level is an SDK concept");
    }

    [Fact]
    public async Task AProfileWithoutATuiStartsAnSdkSessionOnItsReadingLevel()
    {
        var profile = LocalProfile with
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                DefaultReadingLevel = ReadingLevel.Simple,
                OptionDefaults = new Dictionary<string, string> { ["model"] = "llama3.1" },
            },
        };
        var quickStart = Build([profile]);
        var project = Project.Create("Invoices") with { DefaultProfileLabel = "local" };

        var result = await quickStart.ComposeAsync(project);

        result!.Kind.Should().Be(SessionKind.Sdk);
        result.ReadingLevel.Should().Be(ReadingLevel.Simple);
        result.SdkLaunchOptions.Should().ContainKey("model");
        result.PluginTtyOptions.Should().BeNull("the two option vocabularies never both apply to one launch");
    }
}
