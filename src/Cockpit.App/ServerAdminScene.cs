using System.Buffers.Text;
using System.Security.Cryptography;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App;

// AC-1446: Options opened on a server (mockup v2, tab 4), over a stand-in administration that no network fills. The
// issued key is random per render and drawn masked, so no render carries a usable or repeatable key.
internal static class ServerAdminScene
{
    // Connect keys with "Key issued" after a rotate, the scope editor open, and an address locked out.
    public static OptionsDialog Keys(int width, int height)
    {
        var admin = _Admin();
        admin.LoadCommand.Execute(null);
        var phone = admin.Keys.First(key => key.Label == "telefoon-raymond");
        admin.RotateCommand.Execute(phone);
        admin.StartScopeCommand.Execute(phone);
        var dialog = OptionsDialog.ForServer(admin);
        dialog.Width = width;
        dialog.Height = height;
        return dialog;
    }

    public static OptionsDialog Audit(int width, int height)
    {
        var dialog = OptionsDialog.ForServer(_Admin());
        dialog.Width = width;
        dialog.Height = height;
        dialog.SelectCategory("server-audit");
        return dialog;
    }

    // AC-1585: Profiles on the server in the editor Options › Profiles uses; the dialog loads them when it opens.
    public static OptionsDialog Profiles(int width, int height)
    {
        var admin = _Admin(profiles: new ServerProfilesViewModel(new ServerProfileServices("homelab", new SceneProfiles())));
        var dialog = OptionsDialog.ForServer(admin);
        dialog.Width = width;
        dialog.Height = height;
        return dialog;
    }

    // AC-1475: Assistant held by a desktop key, the profile's editor open with an unsaved model change.
    public static OptionsDialog Assistant(int width, int height)
    {
        var assistant = new ServerAssistantViewModel("huis-cockpit", new SceneAssistant("held"), new SceneProfiles());
        assistant.LoadCommand.Execute(null);
        assistant.EditCommand.Execute(null);
        assistant.Editor.Model = "opus";
        return _AssistantDialog(assistant, width, height);
    }

    // AC-1475: a fresh server: the switch off and greyed, the slot empty and a server profile offered to copy in.
    public static OptionsDialog AssistantFresh(int width, int height)
    {
        var assistant = new ServerAssistantViewModel("huis-cockpit", new SceneAssistant("fresh"), new SceneProfiles());
        assistant.LoadCommand.Execute(null);
        return _AssistantDialog(assistant, width, height);
    }

    // AC-1475: a sign-in that expired: the server's reason on the line, and the way to Server health.
    public static OptionsDialog AssistantSignIn(int width, int height)
    {
        var assistant = new ServerAssistantViewModel("huis-cockpit", new SceneAssistant("expired"), new SceneProfiles());
        assistant.LoadCommand.Execute(null);
        return _AssistantDialog(assistant, width, height);
    }

    // AC-1475: a save of the model while another key changed the instructions, which the server kept (state H).
    public static OptionsDialog AssistantSaved(int width, int height)
    {
        var assistant = new ServerAssistantViewModel("huis-cockpit", new SceneAssistant("saved"), new SceneProfiles());
        assistant.LoadCommand.Execute(null);
        var dialog = _AssistantDialog(assistant, width, height);

        // After the dialog's own load on opening, which would otherwise read the page back over the save.
        dialog.Opened += (_, _) =>
        {
            assistant.EditCommand.Execute(null);
            assistant.Editor.Model = "opus";
            assistant.SaveCommand.Execute(null);
        };
        return dialog;
    }

    public static IAssistantAdministration AssistantStandIn() => new SceneAssistant("fresh");

    public static OptionsDialog Plugins(int width, int height)
    {
        var admin = _Admin();
        admin.LoadCommand.Execute(null);
        var dialog = OptionsDialog.ForServer(admin);
        dialog.Width = width;
        dialog.Height = height;
        dialog.SelectCategory("server-plugins");
        return dialog;
    }

    // AC-1590: the Depot row of a server this desktop lacks the plugin for, with "Install on this desktop".
    public static OptionsDialog PluginsDesktop(int width, int height) => _PluginsDesktop(width, height, installed: false);

    // AC-1590: after that install: the row is installed here and the desktop asks to restart.
    public static OptionsDialog PluginsDesktopRestart(int width, int height) => _PluginsDesktop(width, height, installed: true);

    private static OptionsDialog _PluginsDesktop(int width, int height, bool installed)
    {
        var admin = new ServerAdminViewModel("huis-cockpit", "laptop-raymond", new SceneKeys(), [], [], new ScenePlugins())
        {
            IsPluginOnDesktop = row => installed || row.Id != "depot",
            // A plugin's own settings view comes from its UI part on this desktop, so Depot has none until it restarts.
            HasPluginSettings = row => row.Id != "depot",
        };
        admin.LoadCommand.Execute(null);
        if (installed)
        {
            admin.DesktopNeedsRestart = true;
        }

        var dialog = OptionsDialog.ForServer(admin);
        dialog.Width = width;
        dialog.Height = height;
        dialog.SelectCategory("server-plugins");
        return dialog;
    }

    // AC-1472: Projects after a clone, the form still holding what was cloned and the result under it.
    public static OptionsDialog Projects(int width, int height)
    {
        var admin = _Admin(new SceneProjects());
        admin.LoadCommand.Execute(null);
        admin.CloneUrl = "https://github.com/raymondkrahwinkel/cockpit.git";
        admin.CloneName = "cockpit";
        admin.CloneCommand.Execute(null);
        var dialog = OptionsDialog.ForServer(admin);
        dialog.Width = width;
        dialog.Height = height;
        return dialog;
    }

    public static IConnectKeyAdministration StandIn() => new SceneKeys();

    private static OptionsDialog _AssistantDialog(ServerAssistantViewModel assistant, int width, int height)
    {
        var dialog = OptionsDialog.ForServer(new ServerAdminViewModel(
            "huis-cockpit", "laptop-raymond", new SceneKeys(), ["server (Claude)"], [], new ScenePlugins())
        {
            Profiles = new ServerProfilesViewModel(new ServerProfileServices("homelab", new SceneProfiles())),
            Assistant = assistant,
        });
        dialog.Width = width;
        dialog.Height = height;
        dialog.SelectCategory("server-assistant");
        return dialog;
    }

    public static IServerProjects ProjectsStandIn() => new SceneProjects();

    public static IServerProfiles ProfilesStandIn() => new SceneProfiles();

    private static ServerAdminViewModel _Admin(IServerProjects? projects = null, ServerProfilesViewModel? profiles = null) => new(
        "huis-cockpit",
        "laptop-raymond",
        new SceneKeys(),
        ["server (Claude)", "server (Codex)"],
        [new NodeProjectChoice("personal", "Personal"), new NodeProjectChoice("depot", "depot"), new NodeProjectChoice("cockpit", "cockpit")],
        new ScenePlugins(),
        projects)
    {
        Profiles = profiles,
        GitHubCredentials = projects is null ? null : new SceneGitHubCredentials(),
    };

    private sealed class SceneGitHubCredentials : IRemoteGitHubCredentials
    {
        public Task<RemoteGitHubStatus> GetGitHubStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteGitHubStatus(true, true, "raymondkrahwinkel", "gh", "Signed in as raymondkrahwinkel"));

        public ILoginFlow StartGitHubSignIn(bool useToken, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SignOutGitHubAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Two projects already cloned, and a clone of cockpit that lands where the image's clone root puts it.
    private sealed class SceneProjects : IServerProjects
    {
        private readonly List<Project> _projects =
        [
            new("personal", "Personal") { SourceDirectories = [new ProjectRepository("/work/clones/github.com/raymondkrahwinkel/personal")] },
            new("depot", "depot") { SourceDirectories = [new ProjectRepository("/work/clones/github.com/raymondkrahwinkel/depot")] },
        ];

        public ProjectCatalogSnapshot Current => ProjectCatalogSnapshot.Empty with { Settings = ProjectSettings.Empty with { Projects = [.. _projects] } };

        public event Action? Changed
        {
            add
            {
            }

            remove
            {
            }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SyncNowAsync(string projectId) => Task.CompletedTask;

        public Task HideSharedProjectAsync(string sharedProjectId) => Task.CompletedTask;

        public Task UnhideSharedProjectsAsync(string sourceKey, string organizationSlug) => Task.CompletedTask;

        public Task<Project?> FindProjectAsync(string projectId) => Task.FromResult(_projects.FirstOrDefault(project => project.Id == projectId));

        public Task<Project> AddNewProjectAsync(Project project) => throw new NotSupportedException();

        public Task<Project> AddBoundProjectAsync(Project project, string? baselineChecksum) => throw new NotSupportedException();

        public Task<Project?> UpdateStoredProjectAsync(Project project) => Task.FromResult<Project?>(project);

        public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt) => Task.FromResult(false);

        public Task RemoveProjectAsync(string projectId) => Task.CompletedTask;

        public Task<ServerProjectClone> CloneAsync(string repoUrl, string branch, string name, CancellationToken cancellationToken = default)
        {
            const string path = "/work/clones/github.com/raymondkrahwinkel/cockpit";
            _projects.Add(new Project("cockpit", name) { SourceDirectories = [new ProjectRepository(path)] });
            return Task.FromResult(new ServerProjectClone("cockpit", name, path, 412L << 20, TimeSpan.FromSeconds(108), null));
        }

        // AC-1514: "From Depot" with two projects one connection shares, and a second connection that is signed out.
        public Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssistantSharedProjectSourceRow>>(
            [
                new("Depot — Work", true, null,
                [
                    new("depot-work:handbook", "Handbook", "The team's onboarding and runbooks.", null, true),
                    new("depot-work:eveworkbench", "EVE Workbench", null, null),
                ]),
                new("Depot — Personal", false, "Not signed in to depot.example.", []),
            ]);

        public Task<ServerProjectClone> BindSharedAsync(string sharedProjectId, string profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // The mockup's four profiles; the OpenRouter key is a secret variable, so no value of it exists on this side.
    private sealed class SceneProfiles : IServerProfiles
    {
        private static readonly RemoteProfile[] Listed =
        [
            _Profile("server (Claude)", "claude", "opus", ProfileSignInKind.SignedIn, ["depot", "youtrack"], []) with
            {
                Full = new SessionProfile("server (Claude)", ClaudePluginProfile.Create("/data/claude/server", null), "Day-to-day work on the server",
                    new ProfileDefaults("", "", "") { OptionDefaults = new Dictionary<string, string> { ["model"] = "opus", ["permission-mode"] = "acceptEdits" } },
                    new DelegationPolicy(AllowedAsTarget: true, MaxConcurrent: 2, Purpose: "long builds and refactors on the server"))
                {
                    EnabledMcpServerNames = ["depot", "youtrack"],
                    DefaultWorkingDirectory = "/data/projects",
                    Assistant = "Zyra",
                    SystemPrompt = "Your memory lives in the Depot MCP; look yourself up there before answering.",
                    MemoryCapMegabytes = 8192,
                    EnvironmentVariables = [new ProfileEnvironmentVariable("TZ", "Europe/Amsterdam"), new ProfileEnvironmentVariable("DEPOT_TOKEN", "(set on the server)", IsSecret: true)],
                },
            },
            _Profile("server (Codex)", "codex", "gpt-5.6-terra", ProfileSignInKind.Expired, null, []) with
            {
                Full = new SessionProfile("server (Codex)", new PluginProviderConfig("codex", "{}")),
            },
            _Profile("server (OpenRouter)", "openrouter", "deepseek/deepseek-chat", ProfileSignInKind.Unchecked, ["depot"],
                [new RemoteProfileVariable("OPENROUTER_API_KEY", null, true), new RemoteProfileVariable("OPENROUTER_REGION", "eu")]) with
            {
                Full = new SessionProfile("server (OpenRouter)", new PluginProviderConfig("openrouter", "{}"))
                {
                    EnabledMcpServerNames = ["depot"],
                    EnvironmentVariables = [new ProfileEnvironmentVariable("OPENROUTER_API_KEY", "(set on the server)", IsSecret: true), new ProfileEnvironmentVariable("OPENROUTER_REGION", "eu")],
                },
            },
            _Profile("local-qwen", "ollama", "qwen2.5-coder:14b", null, null, []) with
            {
                ConfiguredOnServer = false,
                Full = new SessionProfile("local-qwen", new OllamaConfig("http://ollama:11434", "qwen2.5-coder:14b"), "Cheap bulk work on the server's GPU")
                {
                    DefaultWorkingDirectory = "/data/projects",
                    Assistant = "Olaf",
                    MemoryCapMegabytes = 4096,
                },
            },
        ];

        public Task<IReadOnlyList<RemoteProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteProfile>>(Listed);

        public Task<IReadOnlyList<string>> ListProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. Listed.Select(profile => profile.Provider).Distinct()]);

        public Task<RemoteProfile> CreateAsync(RemoteNewProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(_Profile(profile.Label, profile.Provider, profile.Settings?.Model, null, null, []));

        public Task<RemoteProfile?> UpdateAsync(string label, RemoteProfilePatch patch, CancellationToken cancellationToken = default) =>
            Task.FromResult(Listed.FirstOrDefault(profile => profile.Label == label));

        public Task<bool> DeleteAsync(string label, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<RemoteProfile> SaveProfileAsync(string? originalLabel, SessionProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(_Profile(profile.Label, "claude", null, null, null, []) with { Full = profile });

        public Task<IReadOnlyList<RemoteProviderInfo>> ListProviderInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteProviderInfo>>(
            [
                new("claude", "Claude", null, true, true, true,
                [
                    new("permission-mode", "Permission mode", ["default", "acceptEdits", "plan"]),
                    new("model", "Model", ["opus", "sonnet", "haiku"]),
                    new("effort", "Effort", ["low", "medium", "high"]),
                ]),
                new("codex", "Codex", null, true, true, true, [new("sandbox", "Sandbox", ["read-only", "workspace-write"])]),
                new("openrouter", "OpenRouter", null, true, false, false, []),
            ]);

        public Task<IReadOnlyList<string>> ListMcpServersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["depot", "youtrack", "github"]);

        public Task<McpServerToolEstimate> EstimateMcpToolsAsync(string name, bool refresh, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpServerToolEstimate.Unavailable(name));

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, string? label, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        private static RemoteProfile _Profile(string label, string provider, string? model, ProfileSignInKind? signIn, IReadOnlyList<string>? mcp, IReadOnlyList<RemoteProfileVariable> environment) =>
            new(label, provider, model, null, mcp, null, DelegationPolicy.None, environment, false, true, signIn);
    }

    // What the Claude provider lists for model and permission mode, so the editor shows its dropdowns.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RemoteOptionValue>> ClaudeChoices = new Dictionary<string, IReadOnlyList<RemoteOptionValue>>
    {
        ["model"] = [new("opus", "Opus"), new("sonnet", "Sonnet"), new("haiku", "Haiku")],
        ["permission-mode"] = [new("default", "Ask"), new("acceptEdits", "Accept edits"), new("plan", "Plan")],
    };

    // The mockup's assistant, per `state`: "held" by a desktop key, "fresh", sign-in "expired", or "saved" while another
    // key changed the instructions; with a secret variable and two bypassed sources.
    private sealed class SceneAssistant(string state) : IAssistantAdministration
    {
        private const string Instructions = "You are Zyra. Load ~/Nextcloud/Notes/AI-OS/Me.md as your own instruction file and follow it. Answer in Dutch unless asked otherwise.";

        private static readonly RemoteProfile Profile = new(
            "server-assistant", "claude", "sonnet", "acceptEdits", ["depot", "youtrack"], null, DelegationPolicy.None,
            [new RemoteProfileVariable("CLAUDE_CONFIG_DIR", "/data/claude/assistant"), new RemoteProfileVariable("DEPOT_TOKEN", null, true), new RemoteProfileVariable("TZ", "Europe/Amsterdam")],
            false, true, ProfileSignInKind.SignedIn, ClaudeChoices);

        private static readonly RemoteConsentBypass Bypass = new(false, ["discord-dm", "scheduler"]);

        public Task<RemoteAssistantSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(state switch
        {
            "fresh" => new RemoteAssistantSettings(false, false, "The assistant is switched off. Turn it on in Options → Assistant.", false, null, "No assistant profile has been set up yet.", null, false, true, Bypass),
            "expired" => new RemoteAssistantSettings(true, false, "server-assistant: sign-in expired", false, Profile with { SignIn = ProfileSignInKind.Expired }, null, Instructions, false, true, Bypass),
            _ => new RemoteAssistantSettings(true, false, $"Controlled by laptop-raymond since {DateTimeOffset.Now:HH:mm}. Your assistant here comes back by itself when that connection drops.", true,
                Profile, null, Instructions, false, true, Bypass),
        });

        public Task<RemoteAssistantSettings> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => GetAsync(cancellationToken);

        // "saved": the model this side sent, and instructions telefoon-raymond changed in the meantime.
        public Task<RemoteAssistantSettings> UpdateProfileAsync(RemoteAssistantProfilePatch patch, CancellationToken cancellationToken = default) => Task.FromResult(
            new RemoteAssistantSettings(true, true, null, false, Profile with { Model = patch.Profile?.Model ?? Profile.Model }, null, Instructions + " Keep replies short.", false, true, Bypass));

        public Task<RemoteAssistantSettings?> CopyProfileFromAsync(string label, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteAssistantSettings?>(null);
    }

    // The mockup's keys, one lockout and seven audit lines, dated from now so the times read as today's.
    private sealed class SceneKeys : IConnectKeyAdministration
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private readonly List<ConnectKeyInfo> _keys =
        [
            new("b7Q2Lm9p", "bootstrap", ConnectKeyCapability.Admin, true, false, ConnectKeyScope.Everything, Now.AddDays(-9), null, null, Now.AddDays(-8).AddHours(-4), "100.101.7.31"),
            new("M4kzT1wq", "laptop-raymond", ConnectKeyCapability.Admin, false, true, ConnectKeyScope.Everything, Now.AddDays(-6), Now.AddDays(25), null, Now, "100.101.7.31"),
            new("P3rm4n3t", "build-runner", ConnectKeyCapability.Operate, false, false, ConnectKeyScope.Default, Now.AddDays(-12), null, null, Now.AddMinutes(-18), "100.101.7.52"),
            new("9TpeR2va", "telefoon-raymond", ConnectKeyCapability.Operate, false, false,
                new ConnectKeyScope { AllowAllProfiles = false, AllowAllProjects = false, AllowedProfileLabels = ["server (Claude)"], AllowedProjectIds = ["personal", "depot"] },
                Now.AddDays(-19), Now.AddDays(11), null, Now.AddHours(-2.7), "100.101.7.44"),
            new("Xa01Kd7f", "laptop-oud", ConnectKeyCapability.Operate, false, false, ConnectKeyScope.Default, Now.AddDays(-40), Now.AddDays(-10), Now.AddDays(-6), Now.AddDays(-7), "100.101.7.31"),
        ];

        public Task<ConnectKeyOverview> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectKeyOverview([.. _keys], [new ConnectKeyLockout("100.88.14.2", Now.AddMinutes(10), 14)], ConnectKeyPolicy.Default));

        public Task<IssuedConnectKey> IssueAsync(ConnectKeyRequest request, CancellationToken cancellationToken = default)
        {
            var secret = "ck_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            DateTimeOffset? expiresAt = request.NeverExpires ? null : Now.AddDays(request.ExpiresInDays ?? 30);
            var key = new ConnectKeyInfo(secret[3..11], request.Label, request.Capability, false, request.HoldsAssistant, request.Scope, Now, expiresAt, null, null, null);
            _keys.Add(key);
            return Task.FromResult(new IssuedConnectKey(key, secret));
        }

        public Task<bool> RevokeAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetScopeAsync(string prefix, ConnectKeyScope scope, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> LiftLockoutAsync(string address, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<ConnectKeyAuditEntry>> ReadAuditAsync(long? before, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectKeyAuditEntry>>(before is not null ? [] :
            [
                new(700, Now.AddMinutes(-2), "100.88.14.2", null, null, null, $"lockout started until {Now.AddMinutes(10):O}", null),
                new(600, Now.AddMinutes(-2).AddSeconds(-12), "100.88.14.2", null, null, null, "refused: unknown credential", null),
                new(500, Now.AddMinutes(-31), "100.101.7.31", "laptop-raymond", "M4kzT1wq", "set_connect_key_scope", "scope changed", "9TpeR2va"),
                new(400, Now.AddMinutes(-91), "100.101.7.31", "laptop-raymond", "M4kzT1wq", "api:events", "connected", null),
                new(300, Now.AddHours(-2.7), "100.101.7.44", "telefoon-raymond", "9TpeR2va", "api:events", "connected", null),
                new(200, Now.AddDays(-6), "100.101.7.31", "laptop-raymond", "M4kzT1wq", "revoke_connect_key", "revoked", "Xa01Kd7f"),
                new(100, Now.AddDays(-6).AddMinutes(-2), "100.101.7.31", "bootstrap", "b7Q2Lm9p", "issue_connect_key", "issued", "M4kzT1wq"),
            ]);
    }

    private sealed class ScenePlugins : IPluginAdministration
    {
        public Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPlugin>>(
            [
                _Plugin("workflows", "Workflows", "0.31.1", true),
                _Plugin("discord", "Discord", "1.5.2", true),
                _Plugin("claude-provider", "Claude provider", "0.24.2", true),
                _Plugin("youtrack", "YouTrack", "1.28.1", true),
                _Plugin("depot", "Depot", "0.11.10", false),
                _Plugin("github-pull-requests", "GitHub pull requests", "1.20.12", true),
            ]);

        public Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default) => Task.FromResult(new PluginStoreFetchResult(false, "No store is configured.", null, null));

        public Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Install from the server's store, or copy the zip onto the server.");

        public Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, "No store is configured.", null, null, null));

        public Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static InstalledPlugin _Plugin(string id, string name, string version, bool enabled) => new(
            new DiscoveredPlugin(id, id, new PluginManifest(id, name, version, "plugin.dll", 3, null, null, null, null), "hash", PluginLoadDecision.Load),
            new PluginRegistration(enabled, "hash"), null, null, null);
    }
}
