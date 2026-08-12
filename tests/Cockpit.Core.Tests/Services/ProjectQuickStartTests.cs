using NSubstitute;
using Cockpit.App.Plugins;
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

        return new ProjectQuickStart(profileStore, catalog, ttyProviders, new ProjectMemorySourceRegistry());
    }

    private static McpServerConfig Server(string name, bool enabled = true, bool @internal = false, bool alwaysMounted = false, bool projectLinked = false) =>
        new() { Name = name, Enabled = enabled, Internal = @internal, AlwaysMounted = alwaysMounted, ProjectLinked = projectLinked };

    [Fact]
    public async Task WithoutADefaultProfile_ComposesNothing()
    {
        var quickStart = Build([ClaudeProfile]);

        var result = await quickStart.ComposeAsync(Project.Create("Cockpit"));

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenTheProjectsProfileIsGone_ComposesNothing()
    {
        var quickStart = Build([ClaudeProfile]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "removed" };

        var result = await quickStart.ComposeAsync(project);

        Assert.Null(result);
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

        Assert.NotNull(result);
        Assert.Same(profile, result!.Profile);
        Assert.Equal("Cockpit", result.SessionName);
        Assert.Equal("/home/raymond/RiderProjects/AI-Cockpit", result.WorkingDirectory);
        Assert.True(result.IsolateInWorktree);
        Assert.Equal(project.Id, result.ProjectId);
        Assert.Equal("You are Olaf.\n\nWork ticket by ticket.", result.SystemPrompt);
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

        Assert.Equivalent(new object[] { "depot", "youtrack" }, result!.EnabledMcpServerNames);
    }

    [Fact]
    public async Task AProjectThatSwitchedNothingOff_TicksEveryOfferedServer()
    {
        var quickStart = Build([ClaudeProfile], [Server("depot"), Server("youtrack")]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        Assert.Equivalent(new object[] { "depot", "youtrack" }, result!.EnabledMcpServerNames);
    }

    // AC-736: the Depot server a project's own `depot:` memory row brings in is offered to that project's sessions
    // only, so it never had a row in the project editor's (project-agnostic) checklist to be ticked on — a narrowed
    // list of what is on cannot name it, and before this fix the session it exists for was the one session without it.
    [Fact]
    public async Task AServerTheProjectItselfBroughtIn_TicksEvenThoughTheProjectNarrowedTheRest()
    {
        var quickStart = Build(
            [ClaudeProfile],
            [Server("youtrack"), Server("playwright"), Server("Depot: krahwinkel-it.nl", projectLinked: true)]);
        var project = Project.Create("Cockpit") with
        {
            DefaultProfileLabel = "work",
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["youtrack"] },
        };

        var result = await quickStart.ComposeAsync(project);

        Assert.Equivalent(new object[] { "youtrack", "Depot: krahwinkel-it.nl" }, result!.EnabledMcpServerNames);
    }

    [Fact]
    public async Task LeavesOutTheServersNoChecklistOffers()
    {
        var quickStart = Build(
            [ClaudeProfile],
            [Server("depot"), Server("off", enabled: false), Server("autopilot", @internal: true), Server("cockpit-session", alwaysMounted: true)]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        Assert.Equivalent(new object[] { "depot" }, result!.EnabledMcpServerNames);
    }

    [Fact]
    public async Task ReadsTheCatalogAsTheProjectSeesIt()
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([ClaudeProfile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Server("depot")]);
        var quickStart = new ProjectQuickStart(profileStore, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());
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

        Assert.NotNull(result!.EnabledMcpServerNames);
        Assert.Empty(result.EnabledMcpServerNames);
    }

    [Fact]
    public async Task AProfileWithATuiStartsOne()
    {
        var quickStart = Build([ClaudeProfile]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        Assert.Equal(SessionKind.Tty, result!.Kind);
        Assert.Null(result.ReadingLevel);
    }

    /// <summary>
    /// AC-584. The two tests around this one differ only where <see cref="SessionProfile.DefaultKind"/> plays no
    /// part, so both passed while the quick start asked <c>HasTtyRoute</c> — "is there a TUI at all" — instead of
    /// <c>ResolveDefaultKind</c>, and every Claude profile started as a TTY however it was saved. This is that gap:
    /// a profile that <em>has</em> a TUI and has asked not to use it.
    /// </summary>
    [Fact]
    public async Task AProfileWithATuiThatPrefersTheSdkStartsAnSdkSession()
    {
        var quickStart = Build([ClaudeProfile with { DefaultKind = ProfileSessionKind.Sdk }]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project);

        Assert.Equal(SessionKind.Sdk, result!.Kind);
    }

    /// <summary>
    /// AC-719: the overload a spawn uses, which already knows its profile (the operator or the assistant named it)
    /// rather than asking the project's own <see cref="Project.DefaultProfileLabel"/> to pick one. Everything else —
    /// isolation, the standing prompt, the MCP selection — is the one <see cref="ProjectQuickStart.ComposeAsync"/>
    /// both overloads share, so this only has to pin that the profile requirement drops away.
    /// </summary>
    [Fact]
    public async Task WithAnExplicitProfile_ComposesEvenWithoutADefaultProfileLabel()
    {
        var quickStart = Build([ClaudeProfile]);
        var project = Project.Create("Cockpit") with
        {
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            IsolateInWorktreeByDefault = true,
            BehaviorPrompt = "Work ticket by ticket.",
        };

        var result = await quickStart.ComposeAsync(project, ClaudeProfile);

        Assert.Same(ClaudeProfile, result.Profile);
        Assert.Equal("/home/raymond/RiderProjects/AI-Cockpit", result.WorkingDirectory);
        Assert.True(result.IsolateInWorktree);
        Assert.Equal(project.Id, result.ProjectId);
        Assert.Contains("Work ticket by ticket.", result.SystemPrompt);
    }

    [Fact]
    public async Task WithAnExplicitProfile_IgnoresWhatTheProjectsDefaultProfileLabelSays()
    {
        // The profile named on the call wins even when the project's own default disagrees — a spawn that named
        // "local" explicitly must not be quietly switched to whatever the project would otherwise have picked.
        var quickStart = Build([ClaudeProfile, LocalProfile]);
        var project = Project.Create("Cockpit") with { DefaultProfileLabel = "work" };

        var result = await quickStart.ComposeAsync(project, LocalProfile);

        Assert.Same(LocalProfile, result.Profile);
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

        Assert.Equal(SessionKind.Sdk, result!.Kind);
        Assert.Equal(ReadingLevel.Simple, result.ReadingLevel);
        Assert.Contains("model", result.SdkLaunchOptions!.Keys);
        Assert.Null(result.PluginTtyOptions);
    }
}
