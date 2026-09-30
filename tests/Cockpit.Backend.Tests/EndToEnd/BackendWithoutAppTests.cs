using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Backend.Tests.EndToEnd;

[CollectionDefinition(BackendWithoutAppTests.Alone, DisableParallelization = true)]
public sealed class BackendWithoutAppCollection;

// AC-1381, the closing test of F1 (AC-1368): the backend built by `CockpitBackend` on a fresh state root, with no App,
// no plugins and no stub for any seam. Alone, because the state root is the process environment's.
[Collection(Alone)]
public sealed class BackendWithoutAppTests : IDisposable
{
    public const string Alone = "Backend without App: the process-wide state root";

    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"backend-without-app-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];
    private readonly ILoggerFactory _loggerFactory;

    public BackendWithoutAppTests()
    {
        Directory.CreateDirectory(_stateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _stateRoot);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(_log)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);
        _loggerFactory.Dispose();

        try
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
        catch (IOException)
        {
            // The clone reconcile Start leaves running may still hold a file there; a temp folder the OS clears is fine.
        }
    }

    // The counter-proof: the bootstrap's own launcher registration gone, and the endpoint that starts sessions takes the
    // "Could not start" branch, by name. Not the register: the endpoint host's presence wiring needs it before any
    // endpoint, so without it the host fails outright.
    [Fact]
    public async Task WithoutTheLauncher_TheEndpointThatStartsSessions_TakesTheCouldNotStartBranch_ByName()
    {
        var backend = CockpitBackend.Build(_loggerFactory, services => services.RemoveAll<ISessionLauncher>());
        await using var services = backend.Services;

        backend.Start();

        Assert.Contains("Could not start cockpit MCP endpoint cockpit-node.", _LogText(), StringComparison.Ordinal);
        Assert.DoesNotContain("cockpit-node", services.GetRequiredService<CockpitMcpEndpointHost>().GetServers().Select(server => server.Name));
    }

    // Acceptance 2's red: a planner started before the startup reconcile is refused, and after it the planners run.
    [Fact]
    public async Task ThePlanners_StartOnlyAfterTheStartupReconcile()
    {
        var backend = CockpitBackend.Build(_loggerFactory);
        await using var services = backend.Services;

        var early = Record.Exception(backend.StartPlanners);
        var watchingBeforeStart = services.GetRequiredService<CiWatcher>().Watching;
        backend.Start();
        backend.StartPlanners();

        Assert.IsType<InvalidOperationException>(early);
        Assert.Null(watchingBeforeStart);
        Assert.NotNull(services.GetRequiredService<CiWatcher>().Watching);

        // AC-1381: the backend started with no App in the process, and nothing of the desktop's came along.
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "Cockpit.App");
    }

    // The seams only a frontend filled, answered from Infrastructure: the test registers none of them.
    [Theory]
    [InlineData(typeof(ISessionLauncher))]
    [InlineData(typeof(IProjectEditor))]
    [InlineData(typeof(IAssistantConversation))]
    [InlineData(typeof(IExternalLinkOpener))]
    [InlineData(typeof(IUiHitchProbe))]
    [InlineData(typeof(IDesktopDisplays))]
    public async Task TheSeamsOnlyTheDesktopFilled_ResolveFromInfrastructure(Type seam)
    {
        await using var services = CockpitBackend.Build(_loggerFactory).Services;

        Assert.Equal(typeof(CockpitBackend).Assembly, services.GetRequiredService(seam).GetType().Assembly);
    }

    private string _LogText()
    {
        lock (_log)
        {
            return string.Join("\n", _log);
        }
    }

    private sealed class CollectingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(lines);

        public void Dispose()
        {
        }
    }

    private sealed class CollectingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}
