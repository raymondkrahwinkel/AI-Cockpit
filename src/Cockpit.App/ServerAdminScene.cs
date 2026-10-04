using System.Buffers.Text;
using System.Security.Cryptography;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;

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

    public static IConnectKeyAdministration StandIn() => new SceneKeys();

    private static ServerAdminViewModel _Admin() => new(
        "huis-cockpit",
        "laptop-raymond",
        new SceneKeys(),
        ["server (Claude)", "server (Codex)"],
        [new NodeProjectChoice("personal", "Personal"), new NodeProjectChoice("depot", "depot"), new NodeProjectChoice("cockpit", "cockpit")],
        new ScenePlugins());

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
