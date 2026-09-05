using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1059: <c>AssistantAgentGateway.UpdateProjectAsync</c> — a partial patch onto the stored project, never a
/// rewritten record, so a field the call does not name survives untouched (the exact bug <c>ProjectDialogViewModel
/// .ToProject()</c> shipped for <c>Resources</c>, AC-483). An unknown project id has to be refused as a sentence,
/// not thrown.
/// </summary>
[Collection("avalonia")]
public class AssistantUpdateProjectTests
{
    private const string ProfileLabel = "Zyra";

    [Fact]
    public async Task NamingSomeFields_LeavesEveryOtherStoredFieldExactlyAsItWas()
    {
        var folder = Directory.CreateTempSubdirectory("ac1059-").FullName;
        try
        {
            var existing = Project.Create("Invoices") with
            {
                Description = "Client billing",
                SourceDirectories = [new ProjectRepository(folder)],
                GitUrl = "https://example.test/old.git",
                DefaultProfileLabel = ProfileLabel,
                BehaviorPrompt = "Write in Dutch.",
                IsolateInWorktreeByDefault = true,
                McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["depot"] },
                Category = "Werk",
                MemoryRef = "depot:old-memory",
                PluginFields = new Dictionary<string, string> { ["youtrack.project"] = "AC" },
            };
            var (gateway, projects, _) = _Build(settings: ProjectSettings.Empty with { Projects = [existing] });

            // `category`, `gitUrl` and `memoryRef` are named — every other field this call did not mention must
            // survive by construction (starting from the stored record), not because this test happens to check it.
            var result = await gateway.UpdateProjectAsync(
                existing.Id, category: "Klanten", gitUrl: "https://example.test/new.git", memoryRef: "depot:new-memory");

            Assert.True(result.Ok, result.Error);
            var stored = Assert.Single(projects.Projects);
            Assert.Equal("Klanten", stored.Category);
            Assert.Equal("https://example.test/new.git", stored.GitUrl);
            Assert.Equal("depot:new-memory", stored.MemoryRef);
            Assert.Equal("Invoices", stored.Name);
            Assert.Equal("Client billing", stored.Description);
            Assert.Equal(folder, stored.SourceDirectory);
            Assert.Equal(ProfileLabel, stored.DefaultProfileLabel);
            Assert.Equal("Write in Dutch.", stored.BehaviorPrompt);
            Assert.True(stored.IsolateInWorktreeByDefault);
            Assert.Equal(["depot"], stored.McpOverlay.EnabledServerNames);
            Assert.Equal("AC", stored.PluginFields["youtrack.project"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnknownProjectId_IsRefused_WithAReadableReason_RatherThanThrowing()
    {
        var (gateway, projects, _) = _Build();

        var result = await gateway.UpdateProjectAsync("no-such-project", category: "Klanten");

        Assert.False(result.Ok);
        Assert.Contains("no-such-project", result.Error);
        Assert.Contains("list_projects", result.Error);
        Assert.Empty(projects.Projects);
    }

    // ── Fixtures — trimmed from AssistantCreateProjectTests' own, same shape ──────────────────────────────────

    private static (AssistantAgentGateway Gateway, ProjectsViewModel Projects, IProjectStore Store) _Build(ProjectSettings? settings = null)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? ProjectSettings.Empty);

        var projects = new ProjectsViewModel(store, dialogs: null);
        Dispatcher.UIThread.Invoke(() => projects.LoadAsync()).GetAwaiter().GetResult();

        var gateway = Dispatcher.UIThread.Invoke(() => new AssistantAgentGateway(
            _NewCockpit(projects),
            _Profiles(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(),
            Substitute.For<IPluginProviderRegistry>(),
            new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
            Substitute.For<IAssistantSessionHost>(),
            mcpServerCatalog: _McpCatalog(),
            projectFields: new ProjectFieldRegistry()));

        return (gateway, projects, store);
    }

    private static IMcpServerCatalog _McpCatalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Cockpit.Core.Mcp.McpServerConfig>>([]));
        return catalog;
    }

    private static ISessionProfileStore _Profiles()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile(ProfileLabel, new ClaudeConfig("/home/someone/.claude"))]));
        return profiles;
    }

    private static CockpitViewModel _NewCockpit(ProjectsViewModel projects)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            projects: projects);
    }
}
