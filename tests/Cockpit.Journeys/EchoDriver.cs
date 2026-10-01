using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Journeys;

// The journeys' one fake: the provider, faked below the launcher where a real one spawns a CLI. Answers every
// prompt with "echo: <prompt>" and one completed turn, a failed one for `FailingPrompt`. `Answered` completes when the
// runtime comes back for the next event, which it only does once it has handed the turn's end on.
public sealed class EchoDriver : ISessionDriver
{
    public const string FailingPrompt = "fail";

    private readonly Channel<string> _prompts = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Answered => _answered.Task;

    public SynchronizationContext? ContextAtSend { get; private set; }

    public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

    public string? SessionId => "echo-conversation";

    public SessionProfile? Profile { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public string? PermissionMode { get; private set; }

    public SessionResume? Resume { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _EchoAsync();

    public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        Profile = profile;
        WorkingDirectory = workingDirectory;
        PermissionMode = permissionMode;
        Resume = resume;
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
            var fails = prompt == FailingPrompt;
            yield return new AssistantTextCompleted { SessionId = SessionId, Text = $"echo: {prompt}" };
            yield return new TurnCompleted
            {
                SessionId = SessionId, Subtype = fails ? "error_during_execution" : "success", Result = $"echo: {prompt}", IsError = fails,
            };
            _answered.TrySetResult();
        }
    }
}

// A driver of its own for every session, the first made up front so a journey can wait on it before it exists.
public sealed class EchoDriverFactory(EchoDriver first) : ISessionDriverFactory
{
    private readonly List<EchoDriver> _made = [];

    public IReadOnlyList<EchoDriver> Made
    {
        get
        {
            lock (_made)
            {
                return [.. _made];
            }
        }
    }

    public ISessionDriver Create(SessionProfile? profile)
    {
        lock (_made)
        {
            var driver = _made.Count == 0 ? first : new EchoDriver();
            _made.Add(driver);
            return driver;
        }
    }
}
