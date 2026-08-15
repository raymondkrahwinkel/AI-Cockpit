using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Security tab's awareness banner (AC-41): the view model turns the service's "should warn" into the bound
/// flag the banner reads, and dismissing both hides it and tells the service so it stays hidden.
/// </summary>
public class SecurityOptionsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_MapsTheServicesWarning_OntoTheBanner()
    {
        var vm = new SecurityOptionsViewModel(new FakeProtection { Warn = true });

        await vm.RefreshAsync();

        Assert.True(vm.ShowUnprotectedBanner);
    }

    [Fact]
    public async Task RefreshAsync_LeavesTheBannerDown_WhenTheServiceDoesNotWarn()
    {
        var vm = new SecurityOptionsViewModel(new FakeProtection { Warn = false });

        await vm.RefreshAsync();

        Assert.False(vm.ShowUnprotectedBanner);
    }

    [Fact]
    public async Task DismissBanner_HidesItAtOnce_AndPersistsTheDismissal()
    {
        var protection = new FakeProtection { Warn = true };
        var vm = new SecurityOptionsViewModel(protection);
        await vm.RefreshAsync();

        await vm.DismissBannerCommand.ExecuteAsync(null);

        Assert.False(vm.ShowUnprotectedBanner, "the operator dismissed it");
        Assert.Equal(1, protection.DismissCalls);
    }

    [Fact]
    public async Task TogglingTheNodeSwitch_KeepsThePairingRecord()
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings
        {
            Enabled = true,
            SharedSecret = "granted-by-pairing",
            Pairing = new NodePairing
            {
                ControllerName = "Raymond's desktop",
                ControllerAddress = "192.168.1.5",
                PairedAtUtc = DateTimeOffset.UnixEpoch,
            },
        });

        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        vm.NodeEndpointEnabled = false;
        await Task.Yield();

        // AC-792: this toggle predates the pairing record and writes the whole section. Constructing a fresh
        // `NodeEndpointSettings` here would erase who the node is paired with every time the switch was flipped,
        // while the broker went on believing the coupling was still there.
        var saved = await store.LoadAsync();
        Assert.False(saved.Enabled);
        Assert.NotNull(saved.Pairing);
        Assert.Equal("Raymond's desktop", saved.Pairing!.ControllerName);
    }

    [Fact]
    public async Task TogglingTheNodeSwitch_DoesNotResurrectAKeyThatWasRevokedMeanwhile()
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = true, SharedSecret = "granted-by-pairing" });
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        // The far end unpaired while this tab sat open: the stored secret is gone, the view model's copy is not.
        await store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "" });

        vm.NodeEndpointEnabled = false;
        await Task.Yield();

        // Writing the cached copy back would put a revoked credential on disk, where the next launch would seed
        // the listener from it — the controller silently let back in.
        var saved = await store.LoadAsync();
        Assert.NotEqual("granted-by-pairing", saved.SharedSecret);
    }

    [Fact]
    public async Task CompletingAPairing_WritesPinnedLocalOnlyRowsAndReplacesAnEarlierPairingsRows()
    {
        var servers = new FakeMcpServerStore(
        [
            new McpServerConfig { Name = "something else", Transport = McpTransport.Stdio, Command = "npx" },
            new McpServerConfig { Name = "laptop · stale-endpoint", Transport = McpTransport.Http, Url = "https://old/mcp" },
        ]);

        var client = new FakePairingClient();
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodePairingClient: client, mcpServers: servers)
        {
            PairWithNodeAddress = "192.168.1.20:7331",
        };

        await vm.StartPairingCommand.ExecuteAsync(null);
        Assert.True(vm.IsComparingPairingCode);
        Assert.Equal("314159", vm.OutgoingPairingCode);

        await vm.ConfirmPairingCodeCommand.ExecuteAsync(null);

        var saved = await servers.LoadAsync();
        var added = saved.Single(server => server.Name == "laptop · cockpit-agents");

        Assert.Equal("AABBCCDD", added.PinnedCertificateFingerprint);
        Assert.Equal("granted-by-pairing", added.ApiKey);
        // Only the in-process tool loop can be told which certificate to trust; a spawned CLI session brings its
        // own HTTP client and would fail the handshake against a self-signed certificate.
        Assert.Equal(McpServerScope.LocalOnly, added.Scope);

        // The earlier pairing's rows are replaced, not appended to — pairing twice must not double the list — and
        // a server that has nothing to do with this node is left alone.
        Assert.DoesNotContain(saved, server => server.Name == "laptop · stale-endpoint");
        Assert.Contains(saved, server => server.Name == "something else");

        // And the comparison panel is gone once it is done, so the code cannot be confirmed a second time.
        Assert.False(vm.IsComparingPairingCode);
    }

    private sealed class FakePairingClient : INodePairingClient
    {
        public Task<NodePairingHandshake> BeginAsync(string address, string controllerName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodePairingHandshake(
                $"https://{address}/", "pairing-id", "claim-token", "laptop", "314159", "AABBCCDD", DateTimeOffset.MaxValue));

        public Task<NodePairingGrant> CompleteAsync(NodePairingHandshake handshake, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodePairingGrant("granted-by-pairing", [new NodeEndpointAddress("cockpit-agents", "https://192.168.1.20:7401/mcp")]));

        public Task UnpairAsync(string address, string sharedSecret, string certificateFingerprint, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeMcpServerStore(IReadOnlyList<McpServerConfig> servers) : IMcpServerStore
    {
        private IReadOnlyList<McpServerConfig> _servers = servers;

        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_servers);

        public Task SaveAsync(IReadOnlyList<McpServerConfig> value, CancellationToken cancellationToken = default)
        {
            _servers = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNodeEndpointSettingsStore(NodeEndpointSettings settings) : INodeEndpointSettingsStore
    {
        private NodeEndpointSettings _settings = settings;

        public Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(NodeEndpointSettings value, CancellationToken cancellationToken = default)
        {
            _settings = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProtection : ISecretProtectionService
    {
        public bool Warn { get; set; }

        public int DismissCalls { get; private set; }

        public Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretProtectionStatus(Enabled: false, Unlocked: false, ShouldWarnUnprotected: Warn));

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default)
        {
            DismissCalls++;
            Warn = false;

            return Task.CompletedTask;
        }

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
