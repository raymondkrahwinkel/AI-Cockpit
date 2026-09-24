using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Plugins;

[CollectionDefinition(BackendPluginStartupTests.Alone, DisableParallelization = true)]
public sealed class BackendPluginStartupCollection;

// AC-1392: the backend without App loads the bundled plugins and runs their backend part, at the moments the desktop
// had them. Alone, because the state root the plugins install into is the process environment's.
[Collection(Alone)]
public sealed class BackendPluginStartupTests : IDisposable
{
    public const string Alone = "Backend plugin startup: the process-wide state root";

    private static readonly string[] Bundled =
    [
        "autopilot", "claude-provider", "clock", "example-companion-tool", "example-workspace", "fan-out", "git-status",
        "transcript-search", "usage-trend",
    ];

    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"backend-plugins-{Guid.NewGuid():N}");

    public BackendPluginStartupTests()
    {
        Directory.CreateDirectory(_stateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _stateRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);

        try
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
        catch (IOException)
        {
            // A plugin's own file may still be held there; a temp folder the OS clears is fine.
        }
    }

    // Acceptance 1: all nine load and initialise with no failure, and a provider, a workflow step and an MCP endpoint
    // land where the backend keeps them — with no App in the process.
    [Fact]
    public async Task TheBackendWithoutApp_LoadsTheNineBundledPlugins_AndTheirRegistrationsLand()
    {
        var mounts = new RecordingEndpointHost();
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance, services => services.AddSingleton<ICockpitMcpEndpointHost>(mounts), PluginStartup.Load);
        await using var services = backend.Services;

        backend.InitializePlugins();

        // Why a plugin is missing first, by name: a failure or an approval it waits for, before the list itself.
        var diagnostics = services.GetRequiredService<PluginDiagnostics>();
        Assert.Empty(diagnostics.Failures.Select(failure => $"{failure.FolderId} ({failure.Phase}): {failure.Error}"));
        Assert.Empty(diagnostics.PendingApprovals.Select(pending => pending.ToString()));
        Assert.Equal(Bundled, services.GetRequiredService<PluginManager>().Loaded.Select(plugin => plugin.FolderId).Order());
        Assert.NotNull(services.GetRequiredService<IPluginProviderRegistry>().Resolve("claude"));
        Assert.Contains("git.branch", services.GetRequiredService<IWorkflowStepRegistry>().Steps.Select(step => step.TypeId));
        Assert.Contains("cockpit-autopilot-merge-gate", mounts.Names);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "Cockpit.App");
    }

    // The counter-proof: without the plugin step nothing is loaded, and the same registries stay empty.
    [Fact]
    public async Task WithoutThePluginStep_NoPluginLoads_AndNothingOfTheirsRegisters()
    {
        var mounts = new RecordingEndpointHost();
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance, services => services.AddSingleton<ICockpitMcpEndpointHost>(mounts));
        await using var services = backend.Services;

        backend.InitializePlugins();

        Assert.Null(services.GetService<PluginManager>());
        Assert.Null(services.GetRequiredService<IPluginProviderRegistry>().Resolve("claude"));
        Assert.Empty(mounts.Names);
    }

    // Acceptance 4: ConfigureServices runs in Build, Initialize in InitializePlugins, and the planners — ahead of the
    // desktop's restore — are refused until Initialize has run.
    [Fact]
    public async Task ThePlanners_StartOnlyAfterThePluginsInitialize()
    {
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance, services => services.AddSingleton<ICockpitMcpEndpointHost>(new RecordingEndpointHost()), PluginStartup.Load);
        await using var services = backend.Services;

        backend.Start();
        var early = Record.Exception(backend.StartPlanners);
        backend.InitializePlugins();
        backend.StartPlanners();

        Assert.IsType<InvalidOperationException>(early);
        Assert.NotNull(services.GetRequiredService<CiWatcher>().Watching);
    }

    private sealed class RecordingEndpointHost : ICockpitMcpEndpointHost
    {
        private readonly List<string> _names = [];

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_names)
                {
                    return [.. _names];
                }
            }
        }

        public Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, bool alwaysMounted = false, CancellationToken cancellationToken = default)
        {
            lock (_names)
            {
                _names.Add(serverName);
            }

            return Task.CompletedTask;
        }
    }
}
