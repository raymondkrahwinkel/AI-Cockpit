using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Wraps a plugin's narrow <see cref="IPluginSessionDriver"/> to satisfy the app's real <see cref="ISessionDriver"/>
/// contract (#45) — the seam that lets <see cref="SessionDriverFactory"/> hand a plugin-backed session to the
/// rest of the app unchanged. The Claude-CLI-only live-control members (permission mode / model / thinking-budget
/// switch, always-allow rule persistence) have no equivalent in the narrow interface and are deliberate no-ops
/// here, gated off in the UI by <see cref="Capabilities"/> reporting them unsupported.
/// </summary>
internal sealed class PluginSessionDriverAdapter(IPluginSessionDriver inner, PluginSessionCapabilities pluginCapabilities) : ISessionDriver
{
    // Live model switch / plan mode / thinking budget have no equivalent on the narrow IPluginSessionDriver
    // surface (no members could back them — see PluginSessionCapabilities) — always unsupported here rather
    // than a flag a plugin could set true with nothing behind it (#45 review finding 3). SupportsVision is
    // mapped straight through instead of forced false: every built-in example plugin already reports it
    // false (IPluginSessionDriver.SendUserMessageAsync has no images parameter yet, #64 fase 2), so this
    // stays honest without another host-side change once that surface can actually carry images.
    public SessionCapabilities Capabilities { get; } = new(
        SupportsTools: pluginCapabilities.SupportsTools,
        SupportsPermissions: pluginCapabilities.SupportsPermissions,
        SupportsLiveModelSwitch: false,
        SupportsPlanMode: false,
        SupportsThinking: false,
        SupportsVision: pluginCapabilities.SupportsVision);

    public string? SessionId => inner.SessionId;

    public SessionProfile? Profile { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _AdaptEventsAsync();

    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, CancellationToken cancellationToken = default)
    {
        // workingDirectory and resume are passed through now (#45 D5): a plugin driver that spawns a CLI (Codex
        // app-server) runs in a cwd and resumes a thread by id, and the cockpit already knows both — dropping
        // them here is what made the Codex plugin ask the operator for a working directory it already had. A
        // driver with no cwd/history of its own (an HTTP provider) simply ignores them. Only BySessionId resume
        // crosses the narrow surface; MostRecent needs a provider-side "list newest" step (increment 2).
        Profile = profile;
        var resumeSessionId = resume is { Mode: SessionResumeMode.BySessionId, SessionId: { Length: > 0 } sessionId } ? sessionId : null;
        await inner.StartAsync(model, workingDirectory, resumeSessionId, cancellationToken).ConfigureAwait(false);
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        inner.SendUserMessageAsync(text, cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        inner.InterruptAsync(cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        inner.RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) =>
        inner.SetAutoApproveToolsAsync(enabled, cancellationToken);

    // No always-allow rule persistence in the narrow plugin surface — approve the single outstanding
    // decision only, same as an operator clicking "Allow" once, rather than silently dropping the call.
    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) =>
        inner.RespondToPermissionAsync(toolUseId, allow: true, cancellationToken);

    // No live control channel behind the narrow interface — these Claude-CLI-only operations are deliberate no-ops.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private async IAsyncEnumerable<SessionEvent> _AdaptEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var pluginEvent in inner.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return _Adapt(pluginEvent);
        }
    }

    private static SessionEvent _Adapt(PluginSessionEvent pluginEvent) => pluginEvent switch
    {
        PluginSessionInitialized initialized => new SessionInitialized
        {
            SessionId = initialized.SessionId,
            Cwd = string.Empty,
            Tools = initialized.Tools,
        },
        PluginAssistantTextDelta delta => new AssistantTextDelta
        {
            SessionId = delta.SessionId,
            BlockIndex = delta.BlockIndex,
            Text = delta.Text,
        },
        PluginToolUseRequested toolUse => new ToolUseRequested
        {
            SessionId = toolUse.SessionId,
            ToolUseId = toolUse.ToolUseId,
            ToolName = toolUse.ToolName,
            InputJson = toolUse.InputJson,
        },
        PluginToolResult toolResult => new ToolResult
        {
            SessionId = toolResult.SessionId,
            ToolUseId = toolResult.ToolUseId,
            Content = toolResult.Content,
            IsError = toolResult.IsError,
        },
        PluginPermissionRequested permission => new PermissionRequested
        {
            SessionId = permission.SessionId,
            ToolUseId = permission.ToolUseId,
            ToolName = permission.ToolName,
            InputJson = permission.InputJson,
        },
        PluginTurnCompleted turnCompleted => new TurnCompleted
        {
            SessionId = turnCompleted.SessionId,
            Subtype = turnCompleted.Subtype,
            Result = turnCompleted.Result,
            IsError = turnCompleted.IsError,
            StopReason = turnCompleted.StopReason,
        },
        PluginSessionError error => new SessionError
        {
            SessionId = error.SessionId,
            Message = error.Message,
        },
        _ => new UnknownEvent { SessionId = pluginEvent.SessionId, RawJson = string.Empty },
    };
}
