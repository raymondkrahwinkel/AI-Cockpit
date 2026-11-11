using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.BackendApi;

[CollectionDefinition(RemoteBackendContractTests.Alone, DisableParallelization = true)]
public sealed class RemoteBackendContractCollection;

// AC-1388: the contract suite against `RemoteBackend`, over real HTTPS to the node door of a whole backend whose only
// fake is the provider. Alone, because the state root is the process environment's.
[Collection(Alone)]
public sealed class RemoteBackendContractTests : BackendContractTests
{
    public const string Alone = "Remote backend contract: the process-wide state root";

    protected override async Task<ContractBackend> StartBackendAsync()
    {
        var previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
        var stateRoot = Path.Combine(Path.GetTempPath(), $"remote-contract-{Guid.NewGuid():N}");
        CockpitBackend? backend = null;
        BackendApiClient? client = null;
        RemoteBackend? remote = null;

        // Also run when the start itself fails, so a broken start leaves neither the state root nor a listener behind.
        async ValueTask shutdown()
        {
            try
            {
                if (remote is not null)
                {
                    await remote.DisposeAsync();
                }

                client?.Dispose();
                if (backend is not null)
                {
                    await backend.StopAsync(TimeSpan.FromSeconds(10));
                    await backend.Services.DisposeAsync();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, previousStateRoot);
                try
                {
                    Directory.Delete(stateRoot, recursive: true);
                }
                catch (IOException)
                {
                    // A store's debounced write may still hold a file there; a temp folder the OS clears is fine.
                }
            }
        }

        try
        {
            Directory.CreateDirectory(stateRoot);
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, stateRoot);
            backend = CockpitBackend.Build(NullLoggerFactory.Instance, services => services.AddSingleton<ISessionDriverFactory>(new ContractDrivers()));
            var services = backend.Services;
            var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
            await services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id });
            var profile = new SessionProfile(ContractBackend.Profile, new ClaudeConfig(Path.Combine(stateRoot, "profile"))) { DefaultKind = ProfileSessionKind.Sdk };
            await services.GetRequiredService<ISessionProfileStore>().SaveAsync([profile]);
            await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = Guid.NewGuid().ToString("N"), Port = 0 });
            backend.Start();

            var issuer = new NodeCaller("contract", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None, IssuedToOwner: true);
            var key = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync("contract", ConnectKeyCapability.Admin, 30, issuer, issuedToOwner: true);
            var nodeUrl = new Uri(Assert.Single(services.GetRequiredService<CockpitMcpEndpointHost>().GetNodeAddresses()).Url);
            client = new BackendApiClient(new Uri(nodeUrl, "/"), key.Secret, services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint, TimeProvider.System);
            // A session summary can outlive its handle while removal is in progress.
            var removed = new RemoteSessionHandle(client, "removed", isAssistant: false);
            removed.Update(Assert.IsType<RemoteSessionRow>(JsonSerializer.Deserialize<RemoteSessionRow>(
                """{"paneId":"removed","name":"Removed","workspaceId":null}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            Assert.Empty(removed.WorkspaceId);
            remote = await RemoteBackend.ConnectAsync(client);

            // Criterion 4: a chosen pane or a resume is refused before a request leaves, so the backend starts nothing.
            var registry = services.GetRequiredService<ISessionRegistry>();
            Assert.Contains(remote.Workspaces.Workspaces, workspace => workspace.Id == desk.Id);
            var launch = new SessionLaunchRequest(desk.Id, profile, null, null, null, PaneSessionKind.Sdk, null, null, null, false);
            await Assert.ThrowsAsync<ArgumentException>(() => remote.StartSessionAsync(launch with { PaneId = "chosen" }));
            await Assert.ThrowsAsync<ArgumentException>(() => remote.StartSessionAsync(launch with { Resume = SessionResume.MostRecent }));
            Assert.Empty(registry.All);

            await using (var other = await RemoteBackend.ConnectAsync(client))
            {
                var created = await remote.CreateSessionsWorkspaceAsync("Server workspace");
                await remote.RenameWorkspaceAsync(created.Id, "Renamed on server");
                await _ObservedAsync(other, () => other.Workspaces.Workspaces.Any(workspace => workspace.Id == created.Id && workspace.Name == "Renamed on server"));
                Assert.Equal(created.Id, other.Workspaces.ActiveWorkspaceId);
                await remote.SelectWorkspaceAsync(desk.Id);
                var session = await remote.StartSessionAsync(launch with { WorkspaceId = created.Id });
                Assert.NotNull(session);
                var handle = remote.Find(session.PaneId);
                Assert.NotNull(handle);
                Assert.Equal(created.Id, handle.WorkspaceId);
                var scoped = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync("scoped", ConnectKeyCapability.Admin, 30, issuer,
                    scope: new ConnectKeyScope { AllowAllProfiles = false }, issuedToOwner: true);
                using (var scopedClient = new BackendApiClient(new Uri(nodeUrl, "/"), scoped.Secret, services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint, TimeProvider.System))
                {
                    var snapshot = await scopedClient.GetAsync<WorkspaceSettings>("api/v1/workspaces");
                    Assert.DoesNotContain(snapshot.Workspaces.SelectMany(workspace => workspace.Panes), pane => pane.Id == session.PaneId);
                    var refused = await Assert.ThrowsAsync<BackendApiException>(() => scopedClient.SendAsync<JsonElement>(
                        HttpMethod.Put, $"api/v1/sessions/{session.PaneId}/workspace", new { workspaceId = desk.Id }));
                    Assert.Equal(HttpStatusCode.NotFound, refused.Status);
                }
                Assert.Equal(1, await remote.CloseWorkspaceIfEmptyAsync(created.Id));
                Assert.True(await remote.MoveSessionPaneAsync(session.PaneId, desk.Id));
                await _ObservedAsync(other, () => other.Find(session.PaneId)?.PlacedWorkspaceId == desk.Id);
                Assert.Same(handle, remote.Find(session.PaneId));
                var saved = await services.GetRequiredService<IWorkspaceSettingsStore>().LoadAsync();
                Assert.Contains(saved.Workspaces.Single(workspace => workspace.Id == desk.Id).Panes, pane => pane.Id == session.PaneId);
                Assert.DoesNotContain(saved.Workspaces.Single(workspace => workspace.Id == created.Id).Panes, pane => pane.Id == session.PaneId);
                await using (var reopened = await RemoteBackend.ConnectAsync(client))
                {
                    Assert.Equal(desk.Id, reopened.Find(session.PaneId)?.WorkspaceId);
                    Assert.Equal("Renamed on server", reopened.Workspaces.Workspaces.Single(workspace => workspace.Id == created.Id).Name);
                }
                Assert.Equal(0, await remote.CloseWorkspaceIfEmptyAsync(created.Id));
                await _ObservedAsync(other, () => other.Workspaces.Workspaces.All(workspace => workspace.Id != created.Id));
                await remote.StopSessionAsync(session.PaneId);
            }
            var operate = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync("operate", ConnectKeyCapability.Operate, 30, issuer, issuedToOwner: true);
            using (var operateClient = new BackendApiClient(new Uri(nodeUrl, "/"), operate.Secret, services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint, TimeProvider.System))
            {
                var refused = await Assert.ThrowsAsync<BackendApiException>(() => operateClient.SendAsync<JsonElement>(HttpMethod.Post, "api/v1/workspaces", desk));
                Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
                var addSource = await Assert.ThrowsAsync<BackendApiException>(() => operateClient.SendAsync<JsonElement>(HttpMethod.Post,
                    "api/v1/plugins/store", new { kind = "remote", location = "https://example.invalid/index.json" }));
                Assert.Equal(HttpStatusCode.Forbidden, addSource.Status);
                var removeSource = await Assert.ThrowsAsync<BackendApiException>(() => operateClient.SendAsync<JsonElement>(HttpMethod.Delete,
                    "api/v1/plugins/store/source", null));
                Assert.Equal(HttpStatusCode.Forbidden, removeSource.Status);
                var hideSource = await Assert.ThrowsAsync<BackendApiException>(() => operateClient.SendAsync<JsonElement>(HttpMethod.Put,
                    "api/v1/plugins/store/source/hidden", new { hidden = true }));
                Assert.Equal(HttpStatusCode.Forbidden, hideSource.Status);
            }

            return new ContractBackend(remote, remote, remote, shutdown);
        }
        catch
        {
            await shutdown();
            throw;
        }
    }

    private sealed class ContractDrivers : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => new ContractDriver();
    }

    private static async Task _ObservedAsync(RemoteBackend backend, Func<bool> observed)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void changed(object? sender, EventArgs args)
        {
            if (observed())
            {
                arrived.TrySetResult();
            }
        }
        backend.Changed += changed;
        try
        {
            changed(null, EventArgs.Empty);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            backend.Changed -= changed;
        }
    }
}
