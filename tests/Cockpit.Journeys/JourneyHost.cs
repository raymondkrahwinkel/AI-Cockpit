using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

[CollectionDefinition(Alone, DisableParallelization = true)]
public sealed class JourneyCollection : ICollectionFixture<HeadlessAvalonia>
{
    public const string Alone = "Journeys: the process-wide state root and the one UI thread";
}

// A cockpit on a fresh state root, built by the startup code itself: `Api` the backend alone, `Desktop` the backend
// with Program's own desktop composition and the bundled plugins. The provider is the only fake.
public sealed class JourneyHost : IAsyncDisposable
{
    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly List<string> _log = [];
    private readonly ILoggerFactory _loggerFactory;

    private JourneyHost(Func<ILoggerFactory, EchoDriver, CockpitBackend> build)
    {
        Directory.CreateDirectory(StateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, StateRoot);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(_log)));
        Backend = build(_loggerFactory, Driver);
    }

    public string StateRoot { get; } = Path.Combine(Path.GetTempPath(), $"journey-{Guid.NewGuid():N}");

    public EchoDriver Driver { get; } = new();

    public CockpitBackend Backend { get; }

    public ServiceProvider Services => Backend.Services;

    public string LogText
    {
        get
        {
            lock (_log)
            {
                return string.Join("\n", _log);
            }
        }
    }

    public static JourneyHost Api() => new((loggerFactory, driver) => CockpitBackend.Build(
        loggerFactory, services => services.AddSingleton<ISessionDriverFactory>(new EchoDriverFactory(driver))));

    // As `Program.Main` builds it, up to the window. `beforeBuild` gets the fresh state root first, for what an
    // operator's machine would already hold there (an installed plugin).
    public static JourneyHost Desktop(Action<string>? beforeBuild = null) => new((loggerFactory, driver) =>
    {
        beforeBuild?.Invoke(CockpitBuild.StateRoot);
        var backend = CockpitBackend.Build(
            loggerFactory,
            services =>
            {
                Program.AddDesktop(services, loggerFactory);
                services.AddSingleton<ISessionDriverFactory>(new EchoDriverFactory(driver));
            },
            PluginStartup.Load);
        Program.Services = backend.Services;
        return backend;
    });

    // The bootstrap key as a container hands it over: a file named by the environment, read once and then forgotten.
    public async Task CaptureBootstrapKeyAsync(string key)
    {
        var previous = Environment.GetEnvironmentVariable(ConnectKeyVerifier.BootstrapFileVariable);
        var keyFile = Path.Combine(StateRoot, "connect-key");
        await File.WriteAllTextAsync(keyFile, key);
        Environment.SetEnvironmentVariable(ConnectKeyVerifier.BootstrapFileVariable, keyFile);
        ConnectKeyBootstrapEnvironment.Capture();
        Environment.SetEnvironmentVariable(ConnectKeyVerifier.BootstrapFileVariable, previous);
    }

    public async ValueTask DisposeAsync()
    {
        // The container on the UI thread: the desktop's view models are torn down where they were built.
        await HeadlessAvalonia.RunAsync(async () => await Services.DisposeAsync());
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);
        ConnectKeyBootstrapEnvironment.Forget(ConnectKeyVerifier.BootstrapFileVariable);
        _loggerFactory.Dispose();

        try
        {
            Directory.Delete(StateRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A reconcile or a plugin may still hold a file there; a temp folder the OS clears is fine.
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
