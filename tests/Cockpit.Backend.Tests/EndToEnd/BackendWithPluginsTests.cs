using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Backend.Tests.EndToEnd;

// AC-1403, the end test of F2: the backend built by `CockpitBackend` on a fresh state root with every in-repo plugin
// and no App, as a server runs it. The fixture whose backend part touches Avalonia is refused by name; the rest load,
// and afterwards not one Avalonia, App or plugin UI assembly is in the process.
[Collection(BackendWithoutAppTests.Alone)]
public sealed class BackendWithPluginsTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private const string Fixture = "ui-in-backend";

    private static readonly string[] PluginEndpoints =
    [
        "cockpit-autopilot-ceo", "cockpit-autopilot-merge-gate", "cockpit-autopilot-plan", "cockpit-autopilot-run",
        "cockpit-diagram", "cockpit-docker", "cockpit-github-pull-requests", "cockpit-k8s", "cockpit-kind",
        "cockpit-local-ci", "cockpit-proxmox", "cockpit-whiteboard", "cockpit-wireframe", "cockpit-workflows",
        "cockpit-youtrack",
    ];

    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"backend-with-plugins-{Guid.NewGuid():N}");

    public BackendWithPluginsTests()
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The plugins' assemblies stay loaded for the process, so Windows holds their files; the OS clears temp.
        }
    }

    [Fact]
    public async Task TheBackendLoadsEveryInRepoPlugin_WithoutApp_RefusingTheOneThatTouchesAvalonia()
    {
        var mountLog = new MountLog();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(mountLog));
        var backend = CockpitBackend.Build(loggerFactory, plugins: PluginStartup.Load);
        await using var services = backend.Services;
        var endpoints = services.GetRequiredService<CockpitMcpEndpointHost>();
        var mounted = mountLog.Until(() => PluginEndpoints.All(name => endpoints.GetServers().Any(server => server.Name == name)));

        backend.Start();
        backend.InitializePlugins();
        await Task.WhenAny(mounted, Task.Delay(Ceiling));

        // Without the plugin step the endpoints are missing, by name.
        Assert.Empty(PluginEndpoints.Except(endpoints.GetServers().Select(server => server.Name)));

        using var plugins = services.GetRequiredService<PluginManager>();
        var diagnostics = services.GetRequiredService<PluginDiagnostics>();
        Assert.Equal(
            [$"{Fixture} (initialize): The backend part of plugin UI in backend touches Avalonia.Controls; a backend has no UI."],
            diagnostics.Failures.Select(failure => $"{failure.FolderId} ({failure.Phase}): {failure.Error}"));
        Assert.Empty(diagnostics.PendingApprovals.Select(pending => pending.ToString()));

        // What each in-repo manifest declares: a backend part initialised, a plugin that is only a UI part skipped.
        var manifests = Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, BundledPluginInstaller.BundledFolderName))
            .Select(folder => PluginManifest.TryParse(File.ReadAllText(Path.Combine(folder, "plugin.json")), out var manifest, out _) ? manifest : null)
            .OfType<PluginManifest>()
            .Where(manifest => manifest.Id != Fixture)
            .ToList();
        Assert.Equal(33, manifests.Count);
        Assert.Equal(
            manifests.Where(manifest => manifest.EntryAssembly is not null).Select(manifest => manifest.Id).Order(),
            plugins.LoadedParts.Where(part => part.Plugin is not null && !plugins.InitializeFailed(part.Discovered)).Select(part => part.Discovered.FolderId).Order());
        Assert.Equal(
            manifests.Where(manifest => manifest.EntryAssembly is null).Select(manifest => manifest.Id).Order(),
            plugins.LoadedParts.Where(part => part.Plugin is null).Select(part => part.Discovered.FolderId).Order());

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(AssemblyLoadContext.All.OfType<PluginLoadContext>().SelectMany(context => context.Assemblies))
            .Select(assembly => assembly.GetName().Name ?? string.Empty);
        Assert.Empty(loaded.Where(name => name.StartsWith("Avalonia", StringComparison.Ordinal)
            || name == "Cockpit.App"
            || (name.StartsWith("Cockpit.Plugin.", StringComparison.Ordinal) && name.EndsWith(".UI", StringComparison.Ordinal))).Distinct());
    }

    // The endpoint host logs each mount after listing it, so a log write is the moment to look again.
    private sealed class MountLog : ILoggerProvider, ILogger
    {
        private readonly List<(Func<bool> Condition, TaskCompletionSource Held)> _waits = [];

        public Task Until(Func<bool> condition)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waits)
            {
                _waits.Add((condition, held));
            }

            return held.Task;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Cockpit MCP endpoint ", StringComparison.Ordinal))
            {
                return;
            }

            lock (_waits)
            {
                foreach (var (condition, held) in _waits.Where(wait => wait.Condition()))
                {
                    held.TrySetResult();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Dispose()
        {
        }
    }
}
