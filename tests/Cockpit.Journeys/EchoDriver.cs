using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Journeys;

// The journeys' one fake: the provider, faked below the launcher. Answers a prompt with "echo: <prompt>" and a completed
// turn; a failed one for `FailingPrompt`, and only once interrupted for `WaitingPrompt`. `Answered` completes when the
// runtime comes back for the next event, which it only does once it has handed the turn's end on.
public sealed class EchoDriver : ISessionDriver
{
    public const string FailingPrompt = "fail";

    public const string WaitingPrompt = "wait";

    private readonly Channel<string> _prompts = Channel.CreateUnbounded<string>();
    private readonly Channel<bool> _interrupts = Channel.CreateUnbounded<bool>();
    private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _wedged;

    public EchoDriver(bool wedged = false) => _wedged = wedged;

    // AC-1442: a wedged driver runs a real CLI process and never finishes its teardown, as one that stopped answering.
    public Process? Child { get; private set; }

    public int? ProcessId => Child?.Id;

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
        if (_wedged)
        {
            Child = Process.Start(OperatingSystem.IsWindows()
                ? new ProcessStartInfo("ping", "-n 600 127.0.0.1") { RedirectStandardOutput = true }
                : new ProcessStartInfo("sleep", "600"));
        }

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

    public Task InterruptAsync(CancellationToken cancellationToken = default) => _interrupts.Writer.WriteAsync(true, cancellationToken).AsTask();

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _prompts.Writer.TryComplete();
        return _wedged ? new ValueTask(Task.Delay(Timeout.Infinite)) : ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<SessionEvent> _EchoAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var prompt in _prompts.Reader.ReadAllAsync(cancellationToken))
        {
            // The turn a Stop breaks off ends as the CLI ends it, as an error the pane must not draw as a failure.
            if (prompt == WaitingPrompt)
            {
                await _interrupts.Reader.ReadAsync(cancellationToken);
                yield return new TurnCompleted { SessionId = SessionId, Subtype = "error_during_execution", Result = string.Empty, IsError = true };
                continue;
            }

            var fails = prompt == FailingPrompt;
            var echoed = prompt == "working-directory" ? WorkingDirectory : prompt;
            yield return new AssistantTextCompleted { SessionId = SessionId, Text = $"echo: {echoed}" };
            yield return new TurnCompleted
            {
                SessionId = SessionId, Subtype = fails ? "error_during_execution" : "success", Result = $"echo: {echoed}", IsError = fails,
            };
            _answered.TrySetResult();
        }
    }
}

// A driver of its own for every session, the first made up front so a journey can wait on it before it exists.
public sealed class EchoDriverFactory(EchoDriver first) : ISessionDriverFactory
{
    private readonly List<EchoDriver> _made = [];

    // What every driver after the first is.
    public Func<EchoDriver> Next { get; set; } = () => new EchoDriver();

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
            var driver = _made.Count == 0 ? first : Next();
            _made.Add(driver);
            return driver;
        }
    }
}
