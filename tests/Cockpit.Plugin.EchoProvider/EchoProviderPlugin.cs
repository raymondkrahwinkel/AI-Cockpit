using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
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
            StartLogin = (configJson, _) => new EchoLoginFlow(_SignedInFile(configJson)),
        });
    }

    // AC-1357: a sign-in a journey can take away and give back — signed in while the file the config names exists.
    // A config without `signedInFile` is always signed in.
    private static bool _IsSignedIn(string configJson) =>
        _SignedInFile(configJson) is not { } file || File.Exists(file);

    private static string? _SignedInFile(string configJson) => JsonNode.Parse(configJson)?["signedInFile"]?.GetValue<string>();

    public void Dispose()
    {
    }
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
    public IPluginSessionDriver Create(string configJson) => new EchoSessionDriver();
}

internal sealed class EchoSessionDriver : IPluginSessionDriver
{
    private readonly PluginSessionEventPublisher _events = new();
    private Process? _child;
    private string? _model;

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => "echo-conversation";

    public int? ProcessId => _child?.Id;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    // AC-1473: the model the profile's defaults name, so a journey sees which one a session started with.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        _model = model ?? options?.GetValueOrDefault(WellKnownPluginSessionOptions.Model);
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
        // AC-1467: "ask" stops the turn on a permission prompt, which nobody answers.
        if (text == "ask")
        {
            _events.Publish(new PluginPermissionRequested { SessionId = SessionId, ToolUseId = "echo-ask", ToolName = "Bash", InputJson = "{}" });
            return Task.CompletedTask;
        }

        var answer = _model is null ? $"echo: {text} (cli {_child?.Id})" : $"echo: {text} (cli {_child?.Id}) · model {_model}";
        _events.Publish(new PluginAssistantTextDelta { SessionId = SessionId, BlockIndex = 0, Text = answer });
        _events.Publish(new PluginTurnCompleted { SessionId = SessionId, Subtype = "success", Result = answer, IsError = false });
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
