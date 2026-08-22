using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// AC-971: a delegated session's ceiling has to bind the tools the agent actually writes files with. A CLI-backed
/// provider runs its own Write/Edit/Bash and only asks over its control protocol, so those requests reached the
/// host as a prompt with nobody to answer it — the ceiling bound the host's own tools and nothing else, and a task
/// told in so many words to only read and report rewrote a repository anyway.
/// </summary>
public class DelegatedPluginPermissionGateTests
{
    [Fact]
    public async Task Write_OnAReadOnlyDelegatedSession_IsDeniedWithoutEverPrompting()
    {
        await using var session = await _StartedAsync(ceiling: "default");

        session.Ask("call-1", "Write");
        var (allowed, reason) = await session.AnswerAsync();

        Assert.False(allowed);
        Assert.Contains("Write", reason);

        // The whole point: no prompt was published, because there is no one to see it. A prompt here is the hang.
        Assert.Empty(await session.PromptsSoFarAsync());
    }

    [Fact]
    public async Task Read_OnAReadOnlyDelegatedSession_IsAllowed()
    {
        // Read-only has to be a workable scope, not a disguised stop: a research task still reads, searches and greps.
        await using var session = await _StartedAsync(ceiling: "default");

        session.Ask("call-1", "Read");
        var (allowed, _) = await session.AnswerAsync();

        Assert.True(allowed);
    }

    [Fact]
    public async Task Write_OnAnAcceptEditsDelegatedSession_IsAllowed()
    {
        var session = await _StartedAsync(ceiling: "acceptEdits");
        await using var _ = session;

        session.Ask("call-1", "Write");
        var (allowed, _) = await session.AnswerAsync();

        Assert.True(allowed);
    }

    [Fact]
    public async Task Bash_BelowBypass_IsDenied_UnlessTheProfileAllowListsIt()
    {
        // The host cannot see what a command line will do, so a shell is graded destructive and runs only where the
        // operator said so — either by ceiling or by naming it on the profile's allow-list.
        await using var denied = await _StartedAsync(ceiling: "acceptEdits");
        denied.Ask("call-1", "Bash");
        Assert.False((await denied.AnswerAsync()).Allowed);

        await using var allowed = await _StartedAsync(ceiling: "acceptEdits", allowedTools: ["Bash"]);
        allowed.Ask("call-1", "Bash");
        Assert.True((await allowed.AnswerAsync()).Allowed);
    }

    [Fact]
    public async Task AnMcpTool_IsAllowed_BecauseTheEnabledServerSetIsWhatBoundsIt()
    {
        // The host never connected these servers — the CLI did — so there are no annotations to grade them by. What
        // bounds them is the enabled-server set the delegation policy resolved for this task, which the operator
        // controls per profile. Denying them all would be a blanket ban, not a boundary.
        await using var session = await _StartedAsync(ceiling: "default");

        session.Ask("call-1", "mcp__cockpit-worktrees__worktree_list");
        var (allowed, _) = await session.AnswerAsync();

        Assert.True(allowed);
    }

    [Fact]
    public async Task AnUnknownBuiltIn_IsDenied_BecauseHereTheHostsGateIsTheOnlyGate()
    {
        // A tool a newer CLI ships that the host has no class for: fail closed, since nothing else is deciding it.
        await using var session = await _StartedAsync(ceiling: "bypassPermissions");

        session.Ask("call-1", "SomeNewClaudeTool");
        var (allowed, reason) = await session.AnswerAsync();

        Assert.False(allowed);
        Assert.Contains("SomeNewClaudeTool", reason);
    }

    [Fact]
    public async Task WithoutADelegatedGate_ThePromptStillReachesTheOperator()
    {
        // An ordinary interactive session is untouched: the operator answers, as they always did.
        await using var session = await _StartedAsync(ceiling: null);

        session.Ask("call-1", "Write");
        var prompt = await session.NextPromptAsync();

        Assert.Equal("Write", prompt.ToolName);
        Assert.Null(session.Driver.LastResponse);
    }

    private static async Task<GatedSession> _StartedAsync(string? ceiling, IReadOnlyList<string>? allowedTools = null)
    {
        var inner = new PermissionAskingDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: true),
            new McpAuthKey());

        await adapter.StartAsync();
        if (ceiling is not null)
        {
            await adapter.SetDelegatedToolGateAsync(ceiling, allowedTools ?? []);
        }

        return new GatedSession(inner, adapter);
    }

    // A started adapter over a driver that only asks permission, plus a live reader on the adapter's event stream —
    // so a test can tell "answered by the host" from "published as a prompt" rather than infer it from silence.
    private sealed class GatedSession : IAsyncDisposable
    {
        private readonly PermissionAskingDriver _driver;
        private readonly ISessionDriver _adapter;
        private readonly List<SessionEvent> _seen = [];
        private readonly Task _pump;

        public GatedSession(PermissionAskingDriver driver, ISessionDriver adapter)
        {
            _driver = driver;
            _adapter = adapter;

            // Pumped in the background, the way SessionRuntime pumps a live session: the adapter's stream is a lazy
            // iterator, so nothing it does — including deciding a delegated permission request — happens until
            // somebody enumerates it.
            _pump = Task.Run(async () =>
            {
                await foreach (var evt in _adapter.Events)
                {
                    lock (_seen)
                    {
                        _seen.Add(evt);
                    }
                }
            });
        }

        public PermissionAskingDriver Driver => _driver;

        public void Ask(string toolUseId, string toolName) => _driver.Ask(toolUseId, toolName);

        public async Task<(bool Allowed, string Reason)> AnswerAsync()
        {
            var answer = await _driver.NextResponseAsync();
            return (answer.Allow, answer.DenyReason ?? string.Empty);
        }

        public async Task<PermissionRequested> NextPromptAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (_Prompts() is [var prompt, ..])
                {
                    return prompt;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("No permission prompt reached the host within 5s.");
        }

        // What the stream has published so far. Read after the driver has its answer, by which point a prompt that
        // was going to be published already has been — a short settle for the pump, not a wait for something.
        public async Task<IReadOnlyList<PermissionRequested>> PromptsSoFarAsync()
        {
            await Task.Delay(100);
            return _Prompts();
        }

        private IReadOnlyList<PermissionRequested> _Prompts()
        {
            lock (_seen)
            {
                return [.. _seen.OfType<PermissionRequested>()];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _adapter.DisposeAsync();
            await _pump;
        }
    }

    // A plugin driver that raises permission requests and records how they were answered — the Claude CLI's
    // can_use_tool round trip, with nothing else in the way.
    private sealed class PermissionAskingDriver : IPluginSessionDriver
    {
        private readonly PluginSessionEventPublisher _events = new();
        private readonly TaskCompletionSource<Response> _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed record Response(string ToolUseId, bool Allow, string? DenyReason);

        public Response? LastResponse { get; private set; }

        public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: true);

        public string? SessionId { get; private set; }

        public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

        public void Ask(string toolUseId, string toolName) =>
            _events.Publish(new PluginPermissionRequested
            {
                SessionId = SessionId ?? "stub",
                ToolUseId = toolUseId,
                ToolName = toolName,
                InputJson = "{}",
            });

        public async Task<Response> NextResponseAsync()
        {
            if (await Task.WhenAny(_answered.Task, Task.Delay(TimeSpan.FromSeconds(5))) != _answered.Task)
            {
                throw new TimeoutException("Nothing answered the permission request within 5s — it was left hanging.");
            }

            return await _answered.Task;
        }

        public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
        {
            SessionId = "stub-session";
            return Task.CompletedTask;
        }

        public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, IPluginToolset? toolset, CancellationToken cancellationToken) =>
            StartAsync(model, cancellationToken);

        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
            RespondToPermissionAsync(toolUseId, allow, answersJson: null, denyReason: null, cancellationToken);

        public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, string? denyReason, CancellationToken cancellationToken)
        {
            LastResponse = new Response(toolUseId, allow, denyReason);
            _answered.TrySetResult(LastResponse);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _events.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
