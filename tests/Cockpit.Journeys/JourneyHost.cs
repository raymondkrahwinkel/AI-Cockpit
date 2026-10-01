using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.App;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using Cockpit.TestSupport;
using CockpitApp = Cockpit.App.App;

namespace Cockpit.Journeys;

[CollectionDefinition(Alone, DisableParallelization = true)]
public sealed class JourneyCollection : ICollectionFixture<HeadlessAvalonia>
{
    public const string Alone = "Journeys: the process-wide state root and the one UI thread";
}

// A cockpit on a state root of its own, built by the startup code itself: `Api` the backend alone, `Desktop` the
// backend with Program's own desktop composition and the bundled plugins. The provider is the only fake.
public sealed class JourneyHost : IAsyncDisposable
{
    public const string EchoProfile = "Echo";

    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly bool _ownsStateRoot;
    private readonly List<string> _log = [];
    private readonly ILoggerFactory _loggerFactory;
    private MainWindow? _window;

    private JourneyHost(string? stateRoot, Func<ILoggerFactory, EchoDriverFactory, CockpitBackend> build)
    {
        _ownsStateRoot = stateRoot is null;
        StateRoot = stateRoot ?? Path.Combine(Path.GetTempPath(), $"journey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(StateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, StateRoot);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(_log)));
        Drivers = new EchoDriverFactory(Driver);
        Backend = build(_loggerFactory, Drivers);
    }

    public string StateRoot { get; }

    // The first session's driver; `Drivers` holds every one made, one per session.
    public EchoDriver Driver { get; } = new();

    public EchoDriverFactory Drivers { get; }

    public CockpitBackend Backend { get; }

    public ServiceProvider Services => Backend.Services;

    public CockpitViewModel Cockpit => Services.GetRequiredService<CockpitViewModel>();

    public MainWindow Window => _window ?? throw new InvalidOperationException("The desktop has not been started.");

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

    public static JourneyHost Api() => new(null, (loggerFactory, drivers) => CockpitBackend.Build(
        loggerFactory, services => services.AddSingleton<ISessionDriverFactory>(drivers)));

    // As `Program.Main` builds it, up to the window. `beforeBuild` gets the state root first, for what an operator's
    // machine would already hold there (an installed plugin); a `stateRoot` of an earlier host is a restart onto it.
    public static JourneyHost Desktop(Action<string>? beforeBuild = null, string? stateRoot = null) => new(stateRoot, (loggerFactory, drivers) =>
    {
        beforeBuild?.Invoke(CockpitBuild.StateRoot);
        var backend = CockpitBackend.Build(
            loggerFactory,
            services =>
            {
                Program.AddDesktop(services, loggerFactory);
                services.AddSingleton<ISessionDriverFactory>(drivers);
            },
            PluginStartup.Load);
        Program.Services = backend.Services;
        return backend;
    });

    // `Program.Main`'s Start, then App's steps in App's order: the main window over the cockpit, the assistant's host
    // and chip, the plugins' phase 2 with their UI parts, the planners, the restore of the saved desks and panes.
    public async Task StartDesktopAsync()
    {
        Backend.Start();
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var app = Application.Current as CockpitApp ?? throw new InvalidOperationException("The headless platform runs no cockpit App.");
            _window = new MainWindow { DataContext = Cockpit };

            // The window a desktop lifetime would hold, for the dialogs; Avalonia takes no lifetime after its setup.
            if (Services.GetRequiredService<ISessionDialogService>() is SessionDialogService dialogs)
            {
                dialogs.OwnerWindow = () => _window;
            }

            _window.Show();
            app.StartAssistant(Cockpit);
            app.InitializePlugins();
            Backend.StartPlanners();
            await CockpitApp.RestoreCockpitAsync(Cockpit, Task.CompletedTask);
        });
    }

    // An SDK profile to start (a TTY one needs a terminal) and one project on `folder`, added through the project editor
    // the Projects page and the assistant both write through (AC-1435).
    public async Task<Project> SaveProfileAndProjectAsync(string folder, bool isolateInWorktree = false)
    {
        var project = Project.Create("Journey") with
        {
            SourceDirectories = [new ProjectRepository(folder)],
            IsolateInWorktreeByDefault = isolateInWorktree,
        };
        await Services.GetRequiredService<ISessionProfileStore>().SaveAsync([new SessionProfile(EchoProfile, new ClaudeConfig(Path.Combine(StateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Sdk }]);
        return await Services.GetRequiredService<IProjectEditor>().AddNewProjectAsync(project);
    }

    // The operator's route to a session: New session, the project, the profile, Start. The real dialog, answered
    // through its own view model once it has opened; returns the pane it opened.
    public async Task<SessionViewModel> StartSessionThroughTheDialogAsync(Project project)
    {
        var opened = new TaskCompletionSource<NewSessionDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<NewSessionDialog>((dialog, _) => opened.TrySetResult(dialog));
        var before = HeadlessAvalonia.Run(() => Cockpit.Sessions.ToList());
        Task launched = Task.CompletedTask;
        await HeadlessAvalonia.RunAsync(() =>
        {
            launched = Cockpit.NewSessionCommand.ExecuteAsync(null);
            return Task.CompletedTask;
        });

        var dialog = await opened.Task.WaitAsync(Until.Ceiling);
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var form = dialog.DataContext as NewSessionDialogViewModel ?? throw new InvalidOperationException("The dialog has no form.");
            form.SelectedProject = form.Projects.First(candidate => candidate.Id == project.Id);
            await form.McpChecklistRefresh;
            form.SelectedProfile = form.Profiles.First(profile => profile.Label == EchoProfile);

            // A worktree needs the folder's repository detected first; that probe answers on its own time.
            await Until.Holds(form, () => !form.IsolateInWorktree || form.IsWorkingDirectoryGitRepo);
            form.ConfirmCommand.Execute(null);
            await launched;
        });

        return HeadlessAvalonia.Run(() => Cockpit.Sessions.Except(before).OfType<SessionViewModel>().Single());
    }

    // An MCP client on one of the cockpit's own endpoints as the session `paneId`, which that endpoint was handed to
    // at launch. A real provider reports that hand-over when it mounts its servers; the echo provider mounts nothing.
    public async Task<McpClient> ConnectAsPaneAsync(string paneId, string serverName, string url)
    {
        Services.GetRequiredService<SessionMcpMounts>().Grant(paneId, [serverName]);
        var token = Services.GetRequiredService<SessionMcpKeyring>().TokenFor(paneId);
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = serverName,
            Endpoint = new Uri(url),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));
    }

    public static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A tool that failed answers in prose; that prose is the reason, so it is what the journey reports.
            throw new InvalidOperationException($"{tool} answered: {text}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // On the UI thread, where the desktop's view models were built. The window lets go of the cockpit first: its
        // controls would otherwise resolve from a disposed container, which the app's own UI-thread net swallows.
        await HeadlessAvalonia.RunAsync(async () =>
        {
            if (_window is not null)
            {
                _window.DataContext = null;
                _window.Hide();
            }

            await Services.DisposeAsync();
        });
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);
        ConnectKeyBootstrapEnvironment.Forget(ConnectKeyVerifier.BootstrapFileVariable);
        _loggerFactory.Dispose();

        if (_ownsStateRoot)
        {
            RemoveStateRoot(StateRoot);
        }
    }

    public static void RemoveStateRoot(string stateRoot)
    {
        try
        {
            TestGitDirectory.Remove(stateRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A loaded plugin's assembly or a reconcile may still hold a file there; a temp folder the OS clears is fine.
        }
    }

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
                lines.Add(exception is null ? formatter(state, exception) : $"{formatter(state, exception)}\n{exception}");
            }
        }
    }
}
