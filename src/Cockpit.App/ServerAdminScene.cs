using System.Buffers.Text;
using System.Security.Cryptography;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

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

    // AC-1473: Profiles with the mockup's four sign-ins and the editor open on the one whose key is a secret.
    public static OptionsDialog Profiles(int width, int height)
    {
        var admin = _Admin(profiles: new ServerProfilesViewModel(new SceneProfiles()));
        var profiles = admin.Profiles ?? throw new InvalidOperationException("The scene's admin has no Profiles page.");
        profiles.LoadCommand.Execute(null);
        profiles.StartEditCommand.Execute(profiles.Rows.First(row => row.Label == "server (OpenRouter)"));
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
        assistant.EditCommand.Execute(null);
        assistant.Editor.Model = "opus";
        assistant.SaveCommand.Execute(null);
        return _AssistantDialog(assistant, width, height);
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
            Profiles = new ServerProfilesViewModel(new SceneProfiles()),
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
    };

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

        public Task<Project?> FindProjectAsync(string projectId) => Task.FromResult(_projects.FirstOrDefault(project => project.Id == projectId));

        public Task<Project> AddNewProjectAsync(Project project) => throw new NotSupportedException();

        public Task<Project> AddBoundProjectAsync(Project project) => throw new NotSupportedException();

        public Task<Project?> UpdateStoredProjectAsync(Project project) => Task.FromResult<Project?>(project);

        public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt) => Task.FromResult(false);

        public Task RemoveProjectAsync(string projectId) => Task.CompletedTask;

        public Task<ServerProjectClone> CloneAsync(string repoUrl, string branch, string name, CancellationToken cancellationToken = default)
        {
            const string path = "/work/clones/github.com/raymondkrahwinkel/cockpit";
            _projects.Add(new Project("cockpit", name) { SourceDirectories = [new ProjectRepository(path)] });
            return Task.FromResult(new ServerProjectClone("cockpit", name, path, 412L << 20, TimeSpan.FromSeconds(108), null));
        }
    }

    // The mockup's four profiles; the OpenRouter key is a secret variable, so no value of it exists on this side.
    private sealed class SceneProfiles : IServerProfiles
    {
        private static readonly RemoteProfile[] Listed =
        [
            _Profile("server (Claude)", "claude", "opus", ProfileSignInKind.SignedIn, ["depot", "youtrack"], []),
            _Profile("server (Codex)", "codex", "gpt-5.6-terra", ProfileSignInKind.Expired, null, []),
            _Profile("server (OpenRouter)", "openrouter", "deepseek/deepseek-chat", ProfileSignInKind.Unchecked, ["depot"],
                [new RemoteProfileVariable("OPENROUTER_API_KEY", null, true), new RemoteProfileVariable("OPENROUTER_REGION", "eu")]),
            _Profile("local-qwen", "ollama", "qwen2.5-coder:14b", null, null, []) with { ConfiguredOnServer = false },
        ];

        public Task<IReadOnlyList<RemoteProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteProfile>>(Listed);

        public Task<RemoteProfile> CreateAsync(RemoteNewProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(_Profile(profile.Label, profile.Provider, profile.Settings?.Model, null, null, []));

        public Task<RemoteProfile?> UpdateAsync(string label, RemoteProfilePatch patch, CancellationToken cancellationToken = default) =>
            Task.FromResult(Listed.FirstOrDefault(profile => profile.Label == label));

        public Task<bool> DeleteAsync(string label, CancellationToken cancellationToken = default) => Task.FromResult(true);

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

    // The mockup's four keys, one lockout and seven audit lines, dated from now so the times read as today's.
    private sealed class SceneKeys : IConnectKeyAdministration
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private readonly List<ConnectKeyInfo> _keys =
        [
            new("b7Q2Lm9p", "bootstrap", ConnectKeyCapability.Admin, true, false, ConnectKeyScope.Everything, Now.AddDays(-9), null, null, Now.AddDays(-8).AddHours(-4), "100.101.7.31"),
            new("M4kzT1wq", "laptop-raymond", ConnectKeyCapability.Admin, false, true, ConnectKeyScope.Everything, Now.AddDays(-6), Now.AddDays(25), null, Now, "100.101.7.31"),
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
            var key = new ConnectKeyInfo(secret[3..11], request.Label, request.Capability, false, request.HoldsAssistant, request.Scope, Now, Now.AddDays(30), null, null, null);
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
