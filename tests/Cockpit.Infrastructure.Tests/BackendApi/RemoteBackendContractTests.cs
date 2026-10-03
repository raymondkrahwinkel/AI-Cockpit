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

            var issuer = new NodeCaller("contract", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);
            var key = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync("contract", ConnectKeyCapability.Operate, 30, issuer);
            var nodeUrl = new Uri(Assert.Single(services.GetRequiredService<CockpitMcpEndpointHost>().GetNodeAddresses()).Url);
            client = new BackendApiClient(new Uri(nodeUrl, "/"), key.Secret, services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint, TimeProvider.System);
            remote = await RemoteBackend.ConnectAsync(client);

            // Criterion 4: a chosen pane or a resume is refused before a request leaves, so the backend starts nothing.
            var registry = services.GetRequiredService<ISessionRegistry>();
            var launch = new SessionLaunchRequest(RemoteBackend.NodeDeskId, profile, null, null, null, PaneSessionKind.Sdk, null, null, null, false);
            await Assert.ThrowsAsync<ArgumentException>(() => remote.StartSessionAsync(launch with { PaneId = "chosen" }));
            await Assert.ThrowsAsync<ArgumentException>(() => remote.StartSessionAsync(launch with { Resume = SessionResume.MostRecent }));
            Assert.Empty(registry.All);

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
}
