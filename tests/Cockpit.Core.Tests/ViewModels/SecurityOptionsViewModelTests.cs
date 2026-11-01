using System.Net;
using System.Net.Sockets;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Security tab's awareness banner (AC-41): the view model turns the service's "should warn" into the bound
/// flag the banner reads, and dismissing both hides it and tells the service so it stays hidden.
/// </summary>
public class SecurityOptionsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_ShowsThePairingPortReasonUnlessTheMcpListenerAlreadyHasOne()
    {
        var pairing = Substitute.For<INodePairingEndpoint>();
        pairing.Address.Returns((string?)null);
        pairing.Error.Returns("Not listening for pairing: port 21288 is already in use.");
        var settings = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = true, SharedSecret = "" });

        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: settings, nodePairingEndpoint: pairing);
        await vm.RefreshAsync();

        Assert.Contains("port 21288 is already in use", vm.NodePairingAddress, StringComparison.Ordinal);

        var mcp = Substitute.For<ICockpitInternalMcpProvider>();
        mcp.GetNodeAddresses().Returns([]);
        mcp.NodeListenerError.Returns("Not listening on the network: port 21289 is already in use.");
        vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: settings, mcpEndpointHosts: [mcp], nodePairingEndpoint: pairing);
        await vm.RefreshAsync();

        Assert.Equal("", vm.NodePairingAddress);
        Assert.Contains("port 21289 is already in use", vm.NodeEndpointNoListenerText, StringComparison.Ordinal);
    }

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

    [Theory]
    // The staged row is the reported case: the Options dialog holds this toggle with SuspendPersistence, so the
    // change handler returned before it ever recomputed what the two address fields say.
    [InlineData(true, "", null, true, "restart Cockpit")]
    [InlineData(false, "", null, true, "restart Cockpit")]
    [InlineData(true, "https://192.168.1.20:7401/mcp", null, false, "")]
    // AC-1288: the host knows the port is taken, and then a restart is exactly what does not help.
    [InlineData(true, "", "Not listening on the network: port 20383 is already in use", true, "already in use")]
    public async Task TurningTheNodeSwitchOn_SaysWhyNoListenerAnswers(
        bool staged, string liveAddress, string? listenerError, bool expectedHasNoListener, string expectedText)
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = false, SharedSecret = "kept" });
        var vm = new SecurityOptionsViewModel(
            new FakeProtection(),
            nodeEndpointSettings: store,
            mcpEndpointHosts: [new FakeNodeAddressHost(liveAddress, listenerError)]);
        await vm.RefreshAsync();
        vm.SuspendPersistence = staged;

        vm.NodeEndpointEnabled = true;
        await Task.Yield();

        Assert.Equal(expectedHasNoListener, vm.NodeEndpointHasNoListener);
        Assert.Contains(expectedText, vm.NodeEndpointNoListenerText, StringComparison.Ordinal);
    }

    // AC-1352: connect writes pinned local rows and replaces the previous connection.
    [Fact]
    public async Task Connect_WritesPinnedLocalOnlyRowsAndReplacesAnEarlierRow()
    {
        const string ConnectUrl = "https://192.168.1.20:7331/mcp";
        var staleUrl = ConnectUrl;
        var servers = new FakeMcpServerStore(
        [
            new McpServerConfig { Name = "something else", Transport = McpTransport.Stdio, Command = "npx" },
            new McpServerConfig { Name = "laptop · stale-endpoint", Transport = McpTransport.Http, Url = staleUrl },
        ]);

        {
            var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeSessions: new FakeNodeProbe(), mcpServers: servers)
            {
                ConnectNodeName = "laptop",
                ConnectHost = "192.168.1.20:7331",
                ConnectKey = "ck_test-key",
                ConnectFingerprint = "AABBCCDD",
            };

            await vm.ConnectToServerCommand.ExecuteAsync(null);

            // Never left standing once a connect succeeds.
            Assert.Equal("", vm.ConnectKey);
        }

        var expectedName = "laptop · cockpit-node";
        var saved = await servers.LoadAsync();
        var added = saved.Single(server => server.Name == expectedName);

        Assert.Equal("AABBCCDD", added.PinnedCertificateFingerprint);
        Assert.Equal("ck_test-key", added.ApiKey);
        // Only the in-process tool loop can be told which certificate to trust; a spawned CLI session brings its
        // own HTTP client and would fail the handshake against a self-signed certificate.
        Assert.Equal(McpServerScope.LocalOnly, added.Scope);
        Assert.Equal(McpTransport.Http, added.Transport);

        // The earlier row is replaced, not appended to — reconnecting must not double the list — and a server
        // that has nothing to do with this node is left alone.
        Assert.DoesNotContain(saved, server => server.Name == "laptop · stale-endpoint");
        Assert.Contains(saved, server => server.Name == "something else");
    }

    /// <summary>
    /// AC-1352 criteria 1 and 4: a connect that does not answer as typed — a pin mismatch, a rejected key, an
    /// unreachable address, or a name that already belongs to a different
    /// address — leaves the registry exactly as it was, and a pin mismatch names both fingerprints.
    /// </summary>
    [Theory]
    [InlineData("pin-mismatch")]
    [InlineData("key-rejected")]
    [InlineData("unreachable")]
    [InlineData("name-clash")]
    public async Task Connect_StoresNothing_UnlessTheServerAnswersAsTyped(string scenario)
    {
        var servers = new FakeMcpServerStore(
        [
            new McpServerConfig { Name = "laptop · cockpit-node", Transport = McpTransport.Http, Url = "https://elsewhere/mcp" },
        ]);
        var before = await servers.LoadAsync();

        var probe = new FakeNodeProbe();
        var nodeName = "phone";

        switch (scenario)
        {
            case "pin-mismatch":
                probe.ThrowOnProbe = new HttpRequestException("tls", new NodeCertificatePinMismatchException("AABBCCDD", "11223344"));
                break;
            case "key-rejected":
                probe.ThrowOnProbe = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
                break;
            case "unreachable":
                probe.ThrowOnProbe = new SocketException((int)SocketError.ConnectionRefused);
                break;
            case "name-clash":
                // "laptop" already belongs to a row pointing elsewhere — a typo here must not overwrite it.
                nodeName = "laptop";
                break;
        }

        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeSessions: probe, mcpServers: servers)
        {
            ConnectNodeName = nodeName,
            ConnectHost = "192.168.1.30:20383",
            ConnectKey = "ck_test-key",
            ConnectFingerprint = "AABBCCDD",
        };

        await vm.ConnectToServerCommand.ExecuteAsync(null);

        var after = await servers.LoadAsync();
        Assert.Equal(before, after);

        if (scenario == "pin-mismatch")
        {
            Assert.Equal("This is not the server you meant. Its certificate does not match the fingerprint you entered. Not connected, and your key was not sent.", vm.ConnectStatus);
            Assert.Equal("AA:BB:CC:DD", vm.ConnectEnteredFingerprint);
            Assert.Equal("11:22:33:44", vm.ConnectPresentedFingerprint);
        }

        if (scenario == "key-rejected")
        {
            Assert.Equal("The server refused this key. Check that you copied the whole key. If it was revoked or has expired, ask for a new one.", vm.ConnectStatus);
        }
    }

    [Fact]
    public async Task RefreshAsync_LoadsTheDiscoveryWhitelist_AsCommaSeparatedText()
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings
        {
            Enabled = true,
            AllowedDiscoveryRanges = ["203.0.113.0/24", "198.51.100.0/24"],
        });

        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        Assert.Equal("203.0.113.0/24, 198.51.100.0/24", vm.AllowedDiscoveryRangesText);
    }

    [Fact]
    public async Task EditingTheDiscoveryWhitelistText_PersistsTheParsedRanges()
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = true });
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        vm.AllowedDiscoveryRangesText = "203.0.113.0/24,  198.51.100.0/24 ,, ";
        await Task.Yield();

        var saved = await store.LoadAsync();
        Assert.Equal(["203.0.113.0/24", "198.51.100.0/24"], saved.AllowedDiscoveryRanges);
    }

    /// <summary>
    /// AC-1286, the reported loss: a refresh that lands while the Options dialog is staging used to overwrite the
    /// tick with the disk value, and Apply then wrote that old value back. The measured sequence was tick → Apply =
    /// True, tick → refresh → Apply = False. Both controls are rows here: an ordinary refresh must leave the staged
    /// tick alone, Cancel's own re-seed must still put the disk value back, and outside staging a refresh seeds as
    /// it always did.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    // Outside staging the handler has already written the tick, so the refresh reads back what the operator chose
    // rather than what was there before — the same guarantee, reached by the other path.
    [InlineData(false, false, true)]
    public async Task ARefreshWhileTheDialogIsStaging_DoesNotCostTheOperatorTheTick(
        bool staged, bool reseedStagedValues, bool expectedOnDisk)
    {
        var store = new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = false, SharedSecret = "kept" });
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();
        vm.SuspendPersistence = staged;

        vm.NodeEndpointEnabled = true;
        await vm.PendingWrites;
        await vm.RefreshAsync(reseedStagedValues);
        vm.SuspendPersistence = false;
        await vm.SaveStagedAsync();

        Assert.Equal(expectedOnDisk, (await store.LoadAsync()).Enabled);
    }

    /// <summary>
    /// AC-1286's second, deterministic loss: the node handlers read-modify-write the same section, and with a store
    /// that takes its time the ranges handler used to load while the switch was still saving and write the old
    /// switch back over it — measured as `Enabled = False` with the ranges landing. The row without ranges is the
    /// positive control: the same switch, the same store, nothing racing it.
    /// </summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("10.0.0.0/8", 1)]
    public async Task TwoNodeSettingsChangedInARow_KeepBoth(string ranges, int expectedRanges)
    {
        var store = new SlowNodeEndpointSettingsStore(TimeSpan.FromMilliseconds(50));
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        vm.NodeEndpointEnabled = true;
        vm.AllowedDiscoveryRangesText = ranges;
        await vm.PendingWrites;

        var settled = await store.LoadAsync();
        Assert.True(settled.Enabled);
        Assert.Equal(expectedRanges, settled.AllowedDiscoveryRanges.Count);
    }

    /// <summary>
    /// Apply writes in two steps — the security stores, then the node section. Clearing the line on any success let
    /// the second step erase the first one's failure, so Apply closed over a setting that was never written with
    /// nothing on screen: the reported bug, surviving inside its own fix.
    /// </summary>
    [Fact]
    public async Task AFailedWriteIsNotErased_ByALaterStepOfTheSameApplyThatSucceeded()
    {
        var vm = new SecurityOptionsViewModel(
            new FakeProtection(),
            new FailableScreenLockSettingsStore { Fails = true },
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(new NodeEndpointSettings { Enabled = false, SharedSecret = "kept" }));
        await vm.RefreshAsync();

        await vm.SaveStagedAsync();

        Assert.Contains("the screen lock is not having it", vm.PersistError, StringComparison.Ordinal);
        Assert.Equal("security", vm.PersistErrorCategoryTag);
    }

    /// <summary>
    /// AC-1286: a write that fails used to end on the dispatcher as an unobserved exception from an
    /// <c>async partial void</c> handler — measured as "the caller saw nothing". The operator now reads what did not
    /// get saved, and the positive control in the same run is the line going away once a write succeeds.
    /// </summary>
    [Fact]
    public async Task AWriteThatFails_IsReportedOnTheTab_AndTheLineGoesAwayWhenTheNextWriteSucceeds()
    {
        var store = new FailableNodeEndpointSettingsStore { Fails = true };
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeEndpointSettings: store);
        await vm.RefreshAsync();

        await vm.SaveStagedAsync();

        Assert.Contains("Not saved", vm.PersistError, StringComparison.Ordinal);
        Assert.Contains("the disk is not having it", vm.PersistError, StringComparison.Ordinal);
        Assert.Equal("swarm", vm.PersistErrorCategoryTag);

        store.Fails = false;
        await vm.SaveStagedAsync();

        Assert.Equal("", vm.PersistError);
        Assert.Null(vm.PersistErrorCategoryTag);
    }

    [Fact]
    public async Task RefreshAsync_CalledAgainBeforeTheFirstCallFinishes_NeverRunsTheTwoRebuildsInterleaved()
    {
        // AC-796: `RefreshAsync` is fired un-awaited from more than one place at startup (`CockpitViewModel`'s own
        // constructor, and again from `App.axaml.cs` once a plugin's declared secret keys are known). Without the
        // gate this pins, a second call's `PairedNodes.Clear()` could run between the first call's `Add` calls,
        // leaving cards outside the collection with a `DispatcherTimer` nothing will ever `Dispose()` again.
        var client = new SequencedNodeSessions();
        var vm = new SecurityOptionsViewModel(new FakeProtection(), nodeSessions: client);

        // Both calls answer with no nodes — the property under test is *when* the second call is allowed to reach
        // `ListNodesAsync`, not what it finds there. An empty result also means the loop that calls
        // `card.StartPolling()` never runs, so this stays a plain gate test rather than one that also has to stand
        // up a live Avalonia dispatcher for a real `DispatcherTimer`.
        var first = vm.RefreshAsync();
        var second = vm.RefreshAsync();

        // The second call is genuinely blocked behind the first, not merely scheduled after it: it has not even
        // reached `ListNodesAsync` yet, because the gate has not let it start its own rebuild. Waiting a beat here
        // (rather than asserting the count at once) gives `second`'s call a chance to run as far as it is allowed
        // to — which, gated correctly, is not as far as `ListNodesAsync`.
        await _WaitUntilAsync(() => client.Requests.Count >= 1);
        Assert.Single(client.Requests);

        client.Requests[0].SetResult([]);
        await first;

        // Only now, after the first call released the gate, does the second call's own rebuild begin — releasing
        // the semaphore schedules its continuation rather than resuming it inline, so this also has to be waited
        // for rather than asserted immediately.
        await _WaitUntilAsync(() => client.Requests.Count >= 2);
        client.Requests[1].SetResult([]);
        await second;
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "Condition did not become true.");
    }


    private sealed class SequencedNodeSessions : INodeSessionsClient
    {
        public List<TaskCompletionSource<IReadOnlyList<string>>> Requests { get; } = [];

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default)
        {
            var request = new TaskCompletionSource<IReadOnlyList<string>>();
            Requests.Add(request);
            return request.Task;
        }

        public Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeSessionsSnapshot(nodeName, [], [], []));

        public (NodeSessionsSnapshot Snapshot, DateTimeOffset AtUtc)? TryGetLastSnapshot(string nodeName) => null;

        public Task<NodeStartResult> StartAsync(
            string nodeName,
            string profileLabel,
            string? projectId = null,
            string? prompt = null,
            string? sessionName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeStartResult(null));

        public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SendPromptAsync(string nodeName, string paneId, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SendMessageAsync(string nodeName, string paneId, string kind, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> RenameAsync(string nodeName, string paneId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<NodeTranscriptRead> ReadTranscriptAsync(string nodeName, string paneId, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeTranscriptRead(null));

        public Task<NodeMemoryRead> ReadMemoryAsync(string nodeName, string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeMemoryRead(""));

        public Task<string?> RememberOnNodeAsync(string nodeName, string text, string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<NodeConnectProbe> ProbeAsync(McpServerConfig row, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeWhoAmI?> ReadWhoAmIAsync(string nodeName, CancellationToken cancellationToken = default) => Task.FromResult<NodeWhoAmI?>(null);
    }

    // AC-1352: a stand-in for the connect dialog's own network call — no real registry lookup, no re-resolve,
    // just whatever ProbeAsync should have done for that row: succeed, or throw exactly the exception a scenario
    // needs the view model to classify.
    private sealed class FakeNodeProbe : INodeSessionsClient
    {
        public Exception? ThrowOnProbe { get; set; }

        public Task<NodeConnectProbe> ProbeAsync(McpServerConfig row, CancellationToken cancellationToken = default) =>
            ThrowOnProbe is { } exception
                ? Task.FromException<NodeConnectProbe>(exception)
                : Task.FromResult(new NodeConnectProbe(row.PinnedCertificateFingerprint ?? "", null));

        public Task<NodeWhoAmI?> ReadWhoAmIAsync(string nodeName, CancellationToken cancellationToken = default) => Task.FromResult<NodeWhoAmI?>(null);

        // A connect that went through reloads the node cards; none are drawn here.
        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (NodeSessionsSnapshot Snapshot, DateTimeOffset AtUtc)? TryGetLastSnapshot(string nodeName) => throw new NotSupportedException();

        public Task<NodeStartResult> StartAsync(string nodeName, string profileLabel, string? projectId = null, string? prompt = null, string? sessionName = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> SendPromptAsync(string nodeName, string paneId, string prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> SendMessageAsync(string nodeName, string paneId, string kind, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> RenameAsync(string nodeName, string paneId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeTranscriptRead> ReadTranscriptAsync(string nodeName, string paneId, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeMemoryRead> ReadMemoryAsync(string nodeName, string scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> RememberOnNodeAsync(string nodeName, string text, string scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class FakeNodeAddressHost(string url, string? listenerError = null) : ICockpitInternalMcpProvider
    {
        public IReadOnlyList<McpServerConfig> GetServers() => [];

        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() =>
            new[] { new NodeEndpointAddress("cockpit-agents", url) }.Where(a => a.Url.Length > 0).ToList();

        public string? NodeListenerError { get; } = listenerError;
    }

    // Slow enough that a second handler's own load lands inside the first one's save — the shape the lost update
    // needed, without a test that depends on how fast this machine is.
    private sealed class SlowNodeEndpointSettingsStore(TimeSpan delay) : INodeEndpointSettingsStore
    {
        private NodeEndpointSettings _settings = new() { Enabled = false, SharedSecret = "kept" };

        public Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public async Task SaveAsync(NodeEndpointSettings value, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            _settings = value;
        }
    }

    private sealed class FailableScreenLockSettingsStore : IScreenLockSettingsStore
    {
        public bool Fails { get; set; }

        public Task<ScreenLockSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScreenLockSettings());

        public Task SaveAsync(ScreenLockSettings settings, CancellationToken cancellationToken = default) =>
            Fails ? throw new InvalidOperationException("the screen lock is not having it") : Task.CompletedTask;
    }

    private sealed class FailableNodeEndpointSettingsStore : INodeEndpointSettingsStore
    {
        public bool Fails { get; set; }

        public Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeEndpointSettings { Enabled = false, SharedSecret = "kept" });

        public Task SaveAsync(NodeEndpointSettings value, CancellationToken cancellationToken = default) =>
            Fails ? throw new InvalidOperationException("the disk is not having it") : Task.CompletedTask;
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
