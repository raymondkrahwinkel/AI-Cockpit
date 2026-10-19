using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.EchoProvider;

// AC-1444: an SDK provider that answers "echo: <prompt> (cli <pid>)". Each session runs a real child process, as a CLI
// would, and ends it when disposed; the pid in the answer lets a journey see that it is gone after the cockpit stopped.
public sealed class EchoProviderPlugin : ICockpitPlugin
{
    public const string ProviderId = "echo-provider.echo";

    public PluginMetadata Metadata { get; } = new(
        Id: "echo-provider",
        DisplayName: "Echo provider",
        Author: "Cockpit",
        Description: "Test fixture: an SDK session provider that answers with an echo.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId,
            "Echo",
            _ => new EchoDriverFactory(),
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false)
            {
                // AC-1473: declared, so the admin API shows and changes it.
                DeclaredOptions = [new PluginSessionOptionDescriptor(WellKnownPluginSessionOptions.Model, "Model")],
            })
        {
            IsLoggedIn = _IsSignedIn,
            CredentialKind = _ => PluginCredentialKind.RenewingLogin,
            StartLogin = (configJson, _) => new EchoLoginFlow(_SignedInFile(configJson)),
        });

        // AC-1524: a terminal route the profile can default to, so a journey can show a host without a terminal runs
        // that profile as SDK. The provider is built to answer "is there a route", but never asked to launch.
        host.AddTtyProvider(new TtyProviderRegistration(
            ProviderId,
            "Echo",
            _ => new EchoTtyProvider(),
            []));

        // AC-1571: what this backend part reads of its settings view's two fields; of the credential only whether it is set.
        host.Channel.Handle(EchoUi.ReadSettings, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            greeting = host.Storage.Get<string>(EchoUi.GreetingKey),
            apiTokenSet = host.Storage.GetSecret(EchoUi.ApiTokenKey) is { Length: > 0 },
        })));

        // AC-1514: a shared-project source as a Depot connection registers one, so a journey adds a shared project on a
        // server without a Depot. Only where the variable names the repository its one definition points at.
        if (Environment.GetEnvironmentVariable(SharedRepositoryVariable) is { Length: > 0 } repository)
        {
            host.AddSharedProjectSource(new EchoSharedProjectSource(repository));
        }
    }

    public const string SharedRepositoryVariable = "COCKPIT_ECHO_SHARED_REPOSITORY";

    // AC-1357: a sign-in a journey can take away and give back — signed in while the file the config names exists.
    // A config without `signedInFile` is always signed in.
    private static bool _IsSignedIn(string configJson) =>
        _SignedInFile(configJson) is not { } file || File.Exists(file);

    private static string? _SignedInFile(string configJson) => JsonNode.Parse(configJson)?["signedInFile"]?.GetValue<string>();

    public void Dispose()
    {
    }
}

// AC-1514: one shared definition that names a repository, an MCP choice and a behaviour, as a Depot project would.
internal sealed class EchoSharedProjectSource(string repository) : ISharedProjectSource
{
    public const string ProjectId = "echo-shared:journey";

    // A definition that names no repository, as Publish writes one for a project without a git remote.
    public const string BareProjectId = "echo-shared:bare";

    public string Key => "echo-shared";

    public string SourceName => "Echo shared";

    public bool CanPublish => false;

    public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SharedProjectListResult.Success(
            [new SharedProject(ProjectId, "journey-shared"), new SharedProject(BareProjectId, "journey-bare") { HasRepository = false }]));

    public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(id switch
        {
            ProjectId => SharedProjectBindingResult.Success(new SharedProjectBinding("journey-shared")
            {
                GitUrl = repository,
                BehaviorPrompt = "Answer in echoes.",
                Checksum = _Checksum(),
                EnabledMcpServerNames = ["cockpit-sessions"],
            }),
            BareProjectId => SharedProjectBindingResult.Success(new SharedProjectBinding("journey-bare")),
            _ => SharedProjectBindingResult.Failed("No such shared project."),
        });

    // AC-1570: a journey moves the definition's checksum by rewriting this file; without it there is one fixed value.
    private static string _Checksum() =>
        Environment.GetEnvironmentVariable("COCKPIT_ECHO_SHARED_CHECKSUM_FILE") is { Length: > 0 } path && File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : "echo-1";

    public Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken) =>
        Task.FromResult(SharedProjectWriteBackResult.Failed("The echo source is read-only."));

    public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SharedProjectPublishTargetListResult.Failed("The echo source does not publish."));

    public Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken) =>
        Task.FromResult(SharedProjectPublishResult.Failed("The echo source does not publish."));
}

internal sealed class EchoTtyProvider : IPluginTtyProvider
{
    public PluginTtyLaunchSpec BuildLaunch(PluginTtyLaunchContext context) =>
        throw new NotSupportedException("The echo provider has no TUI to run.");
}

// AC-1457: a sign-in with one step that waits for any text and then gives the sign-in back, so a journey can play a
// whole sign-in without a provider CLI.
internal sealed class EchoLoginFlow(string? signedInFile) : ILoginFlow
{
    private readonly TaskCompletionSource<LoginFlowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IAsyncEnumerable<LoginFlowStep> Steps => _Steps();

    public Task<LoginFlowResult> Completion => _completion.Task;

    public Task SubmitAsync(string value, CancellationToken cancellationToken)
    {
        if (signedInFile is not null)
        {
            File.WriteAllText(signedInFile, "");
        }

        _completion.TrySetResult(new LoginFlowResult(true, null));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _completion.TrySetCanceled();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<LoginFlowStep> _Steps()
    {
        yield return new LoginFlowStep("Echo sign-in: send any text to sign in.", null, true);
        await _completion.Task.ConfigureAwait(false);
    }
}

internal sealed class EchoDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson) => new EchoSessionDriver(configJson);
}

// AC-1534: "config" answers with the base URL the profile's config names and whether it holds a key, never the key.
internal sealed class EchoSessionDriver(string configJson) : IPluginSessionDriver
{
    private readonly PluginSessionEventPublisher _events = new();
    private Process? _child;
    private string? _model;
    private string? _workingDirectory;

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => "echo-conversation";

    public int? ProcessId => _child?.Id;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    // AC-1473: the model the profile's defaults name, so a journey sees which one a session started with.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        _model = model ?? options?.GetValueOrDefault(WellKnownPluginSessionOptions.Model);
        _workingDirectory = workingDirectory;
        return StartAsync(_model, cancellationToken);
    }

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        // Its own pipes, not the cockpit's: a child holding those would keep a reader of the cockpit's output waiting.
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 600 127.0.0.1") : new ProcessStartInfo("sleep", "600");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        _child = Process.Start(start);
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        // AC-1467: "ask" stops the turn on a permission prompt, which nobody answers. AC-1476: "ask <x>" asks for another
        // input, and each ask after the first gets its own id, so a journey can tell the prompts apart.
        if (text == "ask" || text.StartsWith("ask ", StringComparison.Ordinal))
        {
            var twice = text.EndsWith(" twice", StringComparison.Ordinal);
            var detail = (twice ? text[..^6] : text)[3..].Trim();
            var input = detail.Length == 0 ? "{}" : $"{{\"command\":\"{detail}\"}}";
            for (var copy = 0; copy < (twice ? 2 : 1); copy++)
            {
                var id = $"echo-{text.Replace(' ', '-')}{(_asked++ == 0 ? "" : $"-{_asked}")}";
                _events.Publish(new PluginPermissionRequested { SessionId = SessionId, ToolUseId = id, ToolName = "Bash", InputJson = input });
            }

            return Task.CompletedTask;
        }

        var echoed = text switch
        {
            "working-directory" => _workingDirectory,
            "config" => _Config(),
            _ => text,
        };
        var answer = _model is null ? $"echo: {echoed} (cli {_child?.Id})" : $"echo: {echoed} (cli {_child?.Id}) · model {_model}";
        _events.Publish(new PluginAssistantTextDelta { SessionId = SessionId, BlockIndex = 0, Text = answer });
        _events.Publish(new PluginTurnCompleted { SessionId = SessionId, Subtype = "success", Result = answer, IsError = false });
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private string _Config()
    {
        var config = JsonNode.Parse(configJson);
        var key = config?["apiKey"]?.GetValue<string>() is { Length: > 0 } ? "set" : "none";
        return $"base {config?["baseUrl"]?.GetValue<string>()} · key {key}";
    }

    private int _asked;

    // AC-1469: the answer to "ask" comes back as the turn's end, so a journey sees that the server got it.
    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        var answer = $"echo: {(allow ? "allowed" : "denied")} {toolUseId}";
        _events.Publish(new PluginAssistantTextDelta { SessionId = SessionId, BlockIndex = 0, Text = answer });
        _events.Publish(new PluginTurnCompleted { SessionId = SessionId, Subtype = "success", Result = answer, IsError = false });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _events.TryComplete();
        if (_child is { HasExited: false })
        {
            _child.Kill(entireProcessTree: true);
        }

        _child?.Dispose();
        return ValueTask.CompletedTask;
    }
}
