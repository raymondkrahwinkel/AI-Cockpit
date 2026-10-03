using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Tests.BackendApi;

// AC-1441: what the frontend asks of a backend, one suite for every implementation of it (AC-1365 §2). A subclass hands
// over the backend, its sessions answered by `ContractDriver`; the in-proc one runs here, F5.6 adds the remote one.
public abstract class BackendContractTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    protected abstract Task<ContractBackend> StartBackendAsync();

    [Fact]
    public async Task AStartedSession_AnswersAPrompt_WithARowOnTheStream()
    {
        await using var backend = await StartBackendAsync();
        var mark = backend.Events.LastSeq;

        var paneId = await _StartAsync(backend);
        var handle = _HandleOf(backend, paneId);
        var control = handle.Control ?? throw new InvalidOperationException($"Pane '{paneId}' has no control.");
        await control.SetModelAsync("contract-model");
        var model = await _RowAsync(backend, mark, paneId, row => _Text(row) == "echo: model contract-model");
        await control.SetPermissionModeAsync("acceptEdits");
        var permissionMode = await _RowAsync(backend, model.Seq, paneId, row => _Text(row) == "echo: permission-mode acceptEdits");
        var sent = await handle.SendPromptAsync("hello");
        var row = await _RowAsync(backend, permissionMode.Seq, paneId, row => _Text(row) == "echo: hello");

        Assert.True(sent);
        Assert.Equal(paneId, row.PaneId);
    }

    [Fact]
    public async Task APermissionPrompt_IsAnsweredThroughTheHandle_AndTheProviderHearsIt()
    {
        await using var backend = await StartBackendAsync();
        var mark = backend.Events.LastSeq;
        var paneId = await _StartAsync(backend);
        var handle = _HandleOf(backend, paneId);

        await handle.SendPromptAsync(ContractDriver.PermissionPrompt);
        var asked = await _RowAsync(backend, mark, paneId, row => _Bool(row, "IsPendingPermission"));
        var pending = await handle.ReadPendingPermissionsAsync();
        var answered = await handle.RespondToPermissionByIdAsync(ContractDriver.ToolUseId, allow: true);
        var heard = await _RowAsync(backend, asked.Seq, paneId, row => _Text(row) == $"allowed: {ContractDriver.ToolUseId}");

        Assert.Contains(pending, permission => permission.ToolUseId == ContractDriver.ToolUseId);
        Assert.True(answered);
        Assert.Empty(await handle.ReadPendingPermissionsAsync());
        Assert.True(heard.Seq > asked.Seq);
    }

    [Fact]
    public async Task ASession_IsInterruptedThroughItsControl_AndTheProviderEffectReachesTheStream()
    {
        await using var backend = await StartBackendAsync();
        var mark = backend.Events.LastSeq;
        var paneId = await _StartAsync(backend);
        var control = _HandleOf(backend, paneId).Control
            ?? throw new InvalidOperationException($"Pane '{paneId}' has no control.");

        await control.InterruptAsync();
        var row = await _RowAsync(backend, mark, paneId, row => _Text(row) == $"echo: {ContractDriver.InterruptMarker}");

        Assert.Equal(paneId, row.PaneId);
    }

    // Resuming is reading the stream again from a seq the reader kept: what came after it, and nothing it already had.
    [Fact]
    public async Task AStoppedSession_LeavesTheRegistry_AndTheStreamResumesAfterTheKeptSeq()
    {
        await using var backend = await StartBackendAsync();
        var mark = backend.Events.LastSeq;
        var paneId = await _StartAsync(backend);
        var handle = _HandleOf(backend, paneId);
        await handle.SendPromptAsync("one");
        var kept = await _RowAsync(backend, mark, paneId, row => _Text(row) == "echo: one");
        await handle.SendPromptAsync("two");
        await _RowAsync(backend, kept.Seq, paneId, row => _Text(row) == "echo: two");

        await backend.Launcher.StopSessionAsync(paneId);
        var resumed = await _ReadUntilAsync(backend, kept.Seq, evt => evt.Kind == "sessions-changed" && backend.Sessions.Find(paneId) is null);

        Assert.Null(backend.Sessions.Find(paneId));
        Assert.All(resumed, evt => Assert.True(evt.Seq > kept.Seq));
        Assert.Contains(resumed, evt => evt.Kind == "row" && evt.PaneId == paneId && _Text(evt) == "echo: two");
        Assert.DoesNotContain(resumed, evt => evt.Kind == "row" && _Text(evt) == "echo: one");
    }

    private static async Task<string> _StartAsync(ContractBackend backend)
    {
        var desk = await backend.Launcher.RunExclusiveAsync(() =>
            backend.Launcher.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions))
            ?? await backend.Launcher.CreateSessionsWorkspaceAsync("Contract");
        var profile = new SessionProfile(ContractBackend.Profile, new ClaudeConfig(Path.Combine(Path.GetTempPath(), "contract-profile")))
        {
            DefaultKind = ProfileSessionKind.Sdk,
        };
        var started = await backend.Launcher.StartSessionAsync(
            new SessionLaunchRequest(desk.Id, profile, null, null, null, PaneSessionKind.Sdk, null, null, null, false) { IsComposed = true });
        return started?.PaneId ?? throw new InvalidOperationException("The backend started no session.");
    }

    private static ISessionHandle _HandleOf(ContractBackend backend, string paneId) =>
        backend.Sessions.Find(paneId) ?? throw new InvalidOperationException($"The registry has no pane '{paneId}'.");

    private static async Task<BackendEvent> _RowAsync(ContractBackend backend, long afterSeq, string paneId, Func<BackendEvent, bool> matches) =>
        (await _ReadUntilAsync(backend, afterSeq, evt => evt.Kind == "row" && evt.PaneId == paneId && matches(evt)))[^1];

    // Everything after `afterSeq` up to and including the first event that ends the wait; a stream that falls silent fails.
    private static async Task<List<BackendEvent>> _ReadUntilAsync(ContractBackend backend, long afterSeq, Func<BackendEvent, bool> ends)
    {
        using var patience = new CancellationTokenSource(Patience);
        var read = new List<BackendEvent>();
        try
        {
            await foreach (var evt in backend.Events.ReadFromAsync(afterSeq, patience.Token))
            {
                read.Add(evt);
                if (ends(evt))
                {
                    return read;
                }
            }
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested)
        {
            // Falls through to the failure below, which says what did arrive.
        }

        throw new TimeoutException($"No awaited event after seq {afterSeq}; read: {string.Join(", ", read.Select(evt => $"{evt.Seq}:{evt.Kind}"))}.");
    }

    private static string? _Text(BackendEvent evt) =>
        evt.Data.TryGetProperty("Row", out var row) && row.TryGetProperty("Text", out var text) ? text.GetString() : null;

    private static bool _Bool(BackendEvent evt, string property) =>
        evt.Data.TryGetProperty("Row", out var row) && row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}

// The three doors the frontend uses, plus whatever the subclass has to shut down after the test.
public sealed record ContractBackend(ISessionLauncher Launcher, ISessionRegistry Sessions, IBackendEventLog Events, Func<ValueTask> Shutdown)
    : IAsyncDisposable
{
    public const string Profile = "Contract";

    public ValueTask DisposeAsync() => Shutdown();
}

// The provider behind every implementation's sessions: echoes a prompt, and for `PermissionPrompt` asks first and
// says what it was told. Whatever the backend does with a session, this is the only thing it can reach below it.
public sealed class ContractDriver : ISessionDriver
{
    public const string InterruptMarker = "interrupted";

    public const string PermissionPrompt = "may I";

    public const string ToolUseId = "tool-1";

    private readonly Channel<string> _prompts = Channel.CreateUnbounded<string>();
    private readonly Channel<bool> _answers = Channel.CreateUnbounded<bool>();

    public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

    public string? SessionId => "contract-conversation";

    public SessionProfile? Profile { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _AnswerAsync();

    public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        Profile = profile;
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        _prompts.Writer.WriteAsync(text, cancellationToken).AsTask();

    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _prompts.Writer.WriteAsync($"permission-mode {mode}", cancellationToken).AsTask();

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        _prompts.Writer.WriteAsync($"model {model}", cancellationToken).AsTask();

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _prompts.Writer.WriteAsync(InterruptMarker, cancellationToken).AsTask();

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _answers.Writer.WriteAsync(allow, cancellationToken).AsTask();

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(toolUseId, true, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _prompts.Writer.TryComplete();
        _answers.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<SessionEvent> _AnswerAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var prompt in _prompts.Reader.ReadAllAsync(cancellationToken))
        {
            var text = $"echo: {prompt}";
            if (prompt == PermissionPrompt)
            {
                yield return new PermissionRequested { SessionId = SessionId, ToolUseId = ToolUseId, ToolName = "Bash", InputJson = "{}" };
                text = await _answers.Reader.ReadAsync(cancellationToken) ? $"allowed: {ToolUseId}" : $"denied: {ToolUseId}";
            }

            yield return new AssistantTextCompleted { SessionId = SessionId, Text = text };
            yield return new TurnCompleted { SessionId = SessionId, Subtype = "success", Result = text, IsError = false };
        }
    }
}
