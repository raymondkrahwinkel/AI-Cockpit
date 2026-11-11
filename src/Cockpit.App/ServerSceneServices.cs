using System.Buffers.Text;
using System.Security.Cryptography;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.App;

// Network-free server data shared by the ordinary remote-window renders.
internal static class ServerSceneServices
{
    public static IConnectKeyAdministration StandIn() => new SceneKeys();
    public static IServerProjects ProjectsStandIn() => new SceneProjects();
    public static IServerProfiles ProfilesStandIn() => new SceneProfiles();
    public static IAssistantAdministration AssistantStandIn() => new SceneAssistant("fresh");

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
                    Assistant = "depot:acme/zyra@depot.acme.example",
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

        // The server's one Depot connection, with two brains of its own.
        public Task<IReadOnlyList<RemoteBrainSource>> ListBrainSourcesAsync(string? scheme = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteBrainSource>>(
            [
                new("depot", "Depot project — Acme", "Acme",
                    [new("acme/zyra@depot.acme.example", "Zyra", "acme/zyra · Acme"), new("acme/olaf@depot.acme.example", "Olaf", "acme/olaf · Acme")], null),
            ]);

        private static RemoteProfile _Profile(string label, string provider, string? model, ProfileSignInKind? signIn, IReadOnlyList<string>? mcp, IReadOnlyList<RemoteProfileVariable> environment) =>
            new(label, provider, model, null, mcp, null, DelegationPolicy.None, environment, false, true, signIn);
    }

    private sealed class SceneAssistant(string state) : IAssistantAdministration
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<RemoteOptionValue>> ClaudeChoices = new Dictionary<string, IReadOnlyList<RemoteOptionValue>>
        {
            ["model"] = [new("opus", "Opus"), new("sonnet", "Sonnet"), new("haiku", "Haiku")],
            ["permission-mode"] = [new("default", "Ask"), new("acceptEdits", "Accept edits"), new("plan", "Plan")],
        };

        private const string Instructions = "You are Zyra. Load ~/Nextcloud/Notes/AI-OS/Me.md as your own instruction file and follow it. Answer in Dutch unless asked otherwise.";

        private static readonly RemoteProfile Profile = new RemoteProfile(
            "server-assistant", "claude", "sonnet", "acceptEdits", ["depot", "youtrack"], null, DelegationPolicy.None,
            [new RemoteProfileVariable("CLAUDE_CONFIG_DIR", "/data/claude/assistant"), new RemoteProfileVariable("DEPOT_TOKEN", null, true), new RemoteProfileVariable("TZ", "Europe/Amsterdam")],
            false, true, ProfileSignInKind.SignedIn, ClaudeChoices) with
        {
            Full = new SessionProfile("server-assistant", ClaudePluginProfile.Create("/data/claude/assistant", null), null,
                new ProfileDefaults("", "", "") { OptionDefaults = new Dictionary<string, string> { ["model"] = "sonnet", ["permission-mode"] = "acceptEdits" } })
            {
                EnabledMcpServerNames = ["depot", "youtrack"],
                Assistant = "depot:acme/zyra@depot.acme.example",
                SystemPrompt = Instructions,
                EnvironmentVariables = [new ProfileEnvironmentVariable("DEPOT_TOKEN", "(set on the server)", IsSecret: true), new ProfileEnvironmentVariable("TZ", "Europe/Amsterdam")],
            },
        };

        private static readonly RemoteConsentBypass Bypass = new(false, ["discord-dm", "scheduler"]);

        public Task<RemoteAssistantSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(state switch
        {
            "fresh" => new RemoteAssistantSettings(false, false, "The assistant is switched off. Turn it on in Options → Assistant.", false, null, "No assistant profile has been set up yet.", null, false, true, Bypass),
            "expired" => new RemoteAssistantSettings(true, false, "server-assistant: sign-in expired", false, Profile with { SignIn = ProfileSignInKind.Expired }, null, Instructions, false, true, Bypass),
            _ => new RemoteAssistantSettings(true, false, $"Controlled by laptop-raymond since {DateTimeOffset.Now:HH:mm}. Your assistant here comes back by itself when that connection drops.", true,
                Profile, null, Instructions, false, true, Bypass),
        });

        public Task<RemoteAssistantSettings> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => GetAsync(cancellationToken);

        // "saved": the server holds the profile the dialog sent, and the assistant runs on it.
        public Task<RemoteAssistantSettings> SaveProfileAsync(SessionProfile profile, bool replacesStandingInstruction, CancellationToken cancellationToken = default, string? assistantRole = null) => Task.FromResult(
            new RemoteAssistantSettings(true, true, null, false, Profile with { Full = profile }, null, profile.SystemPrompt, replacesStandingInstruction, true, Bypass));

        public Task<RemoteAssistantSettings?> CopyProfileFromAsync(string label, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteAssistantSettings?>(null);
    }

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

        public Task<bool> MarkOwnDeviceAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult(true);

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

}
