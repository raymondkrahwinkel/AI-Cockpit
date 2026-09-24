using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Workspaces;
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

    private const string Profile = "Echo";

    private static readonly string[] CoreEndpoints =
    [
        "cockpit-session", "cockpit-verify", "cockpit-agents", "cockpit-assistant", "cockpit-assistant-agents",
        "cockpit-node", "cockpit-worktrees", "cockpit-terminal", "cockpit-shell",
    ];

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
        Directory.Delete(_stateRoot, recursive: true);
    }

    // Acceptance 1–4: every core endpoint mounts; `start_node_agent` over the HTTPS door, with a connect key, starts an
    // SDK session whose answer `read_node_transcript` returns; no Avalonia or App assembly is loaded; nothing is stubbed.
    // The only addition is the provider, and it is faked below the launcher, where a real one spawns a CLI.
    [Fact]
    public async Task TheBackendWithoutApp_MountsEveryCoreEndpoint_AndStartsANodeAgentThroughTheConnectKeyDoor()
    {
        var key = $"ck_{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var keyFile = Path.Combine(_stateRoot, "connect-key");
        await File.WriteAllTextAsync(keyFile, key);
        Environment.SetEnvironmentVariable(ConnectKeyVerifier.BootstrapFileVariable, keyFile);
        ConnectKeyBootstrapEnvironment.Capture();
        Environment.SetEnvironmentVariable(ConnectKeyVerifier.BootstrapFileVariable, null);
        var driver = new EchoDriver();
        var backend = CockpitBackend.Build(_loggerFactory, services => services.AddSingleton<ISessionDriverFactory>(new EchoDriverFactory(driver)));
        await using var services = backend.Services;
        await _SaveAFreshNodeAsync(services);

        backend.Start();
        backend.StartPlanners();
        var host = services.GetRequiredService<CockpitMcpEndpointHost>();
        var nodeUrl = Assert.Single(host.GetNodeAddresses()).Url;
        await using var client = await McpClient.CreateAsync(NodeCertificatePin.TransportFor(
            new McpServerConfig { Name = "node", Transport = McpTransport.Http, Url = nodeUrl, PinnedCertificateFingerprint = services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint },
            new HttpClientTransportOptions { Endpoint = new Uri(nodeUrl), AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" } }));
        var started = await _CallAsync(client, "start_node_agent", new() { ["profile"] = Profile, ["prompt"] = "hello" });
        await driver.Answered.WaitAsync(TimeSpan.FromSeconds(30));
        var transcript = await _CallAsync(client, "read_node_transcript", new() { ["paneId"] = started["paneId"]?.GetValue<string>() });

        Assert.Superset(CoreEndpoints.ToHashSet(), host.GetServers().Select(server => server.Name).ToHashSet());
        Assert.DoesNotContain(_LogText(), "Could not start cockpit MCP endpoint", StringComparison.Ordinal);
        Assert.True(started["ok"]?.GetValue<bool>());
        Assert.Contains(
            "AssistantText: echo: hello",
            (transcript["entries"]?.AsArray() ?? []).Select(entry => $"{entry?["kind"]?.GetValue<string>()}: {entry?["text"]?.GetValue<string>()}"));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "Cockpit.App");

        // AC-1379's threading edge, measured: with no App there is no UI thread, and a node call reaches the session
        // on a thread with no synchronization context to return to.
        Assert.Null(driver.ContextAtSend);
    }

    // The counter-proof: one backend registration gone, and the endpoints that need it take the "Could not start"
    // branch, by name, instead of the whole backend failing or failing quietly.
    [Fact]
    public async Task WithoutTheSessionRegistry_AnEndpointThatNeedsIt_TakesTheCouldNotStartBranch_ByName()
    {
        var backend = CockpitBackend.Build(_loggerFactory, services => services.RemoveAll<ISessionRegistry>());
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

    // A desk to land on, the profile to run and the node door open on a port of the OS's choosing.
    private static async Task _SaveAFreshNodeAsync(IServiceProvider services)
    {
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        await services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id });
        await services.GetRequiredService<ISessionProfileStore>().SaveAsync([new SessionProfile(Profile, new ClaudeConfig("/fake/.claude"))]);
        await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = Guid.NewGuid().ToString("N"), Port = 0 });
    }

    private static async Task<JsonNode> _CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        return JsonNode.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text))) ?? new JsonObject();
    }

    private string _LogText()
    {
        lock (_log)
        {
            return string.Join("\n", _log);
        }
    }

    private sealed class EchoDriverFactory(EchoDriver driver) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => driver;
    }

    // A provider that answers every prompt with "echo: <prompt>" and one completed turn. `Answered` completes when the
    // runtime comes back for the next event, which it only does once it has handed the turn's end on.
    private sealed class EchoDriver : ISessionDriver
    {
        private readonly Channel<string> _prompts = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Answered => _answered.Task;

        public SynchronizationContext? ContextAtSend { get; private set; }

        public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

        public string? SessionId => "echo-conversation";

        public SessionProfile? Profile { get; private set; }

        public IAsyncEnumerable<SessionEvent> Events => _EchoAsync();

        public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
        {
            Profile = profile;
            return Task.CompletedTask;
        }

        public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default)
        {
            ContextAtSend = SynchronizationContext.Current;
            return _prompts.Writer.WriteAsync(text, cancellationToken).AsTask();
        }

        public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _prompts.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<SessionEvent> _EchoAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var prompt in _prompts.Reader.ReadAllAsync(cancellationToken))
            {
                yield return new AssistantTextCompleted { SessionId = SessionId, Text = $"echo: {prompt}" };
                yield return new TurnCompleted { SessionId = SessionId, Subtype = "success", Result = $"echo: {prompt}", IsError = false };
                _answered.TrySetResult();
            }
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
