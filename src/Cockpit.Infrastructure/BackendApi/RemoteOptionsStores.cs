using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Clones;
using Cockpit.Core.Debugging;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Shell;
using Cockpit.Core.Swarm;
using Cockpit.Core.Terminal;
using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.BackendApi;

// Resolve the connection on use: the ordinary remote window is composed before its registry has connected.
public sealed class RemoteOptionsStores(Func<IRemoteServer?> server) : IRemoteOptionsStores, IAssistantSettingsStore
{
    private BackendApiClient Client => server() is RemoteServer remote
        ? remote.OptionsClient
        : throw new InvalidOperationException("The server is not connected. Reopen Options after connecting.");

    private NotificationSettings _clientNotifications = new();
    private DebugSettings _clientDebug = new();
    private TerminalSettings _clientTerminal = new();
    private readonly Notifications.NotificationSettingsStore _notifications = new();
    private readonly Debugging.DebugSettingsStore _debug = new();
    private readonly Terminal.TerminalSettingsStore _terminal = new();
    private string _cloneDefault = string.Empty;
    private string _worktreeDefault = string.Empty;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _confirmed = new(StringComparer.Ordinal);
    private bool _confirmedCleanup;

    public bool ReadConfirmedValues { get; set; }

    public IAssistantSettingsStore Assistant => this;

    private readonly Assistant.AssistantSettingsStore _assistant = new();
    private AssistantSettings _clientAssistant = new();

    async Task<AssistantSettings> IAssistantSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        _clientAssistant = await _assistant.LoadAsync(cancellationToken).ConfigureAwait(false);
        var value = await _ReadAsync<AssistantServerOptions>("assistant", cancellationToken).ConfigureAwait(false);
        return _clientAssistant with
        {
            IsEnabled = value.IsEnabled, ReadingLevel = value.ReadingLevel,
            ContextCompactionPercent = value.ContextCompactionPercent, SpeechProfileLabel = value.SpeechProfileLabel,
            ConsentBypassAll = value.ConsentBypassAll, ConsentBypassSources = value.ConsentBypassSources,
            ConsentBypassDangerousSources = value.ConsentBypassDangerousSources, RequireRole = value.RequireRole,
        };
    }

    async Task IAssistantSettingsStore.SaveAsync(AssistantSettings settings, CancellationToken cancellationToken)
    {
        await _WriteAsync("assistant", new AssistantServerOptions(settings.IsEnabled, settings.ReadingLevel,
            settings.ContextCompactionPercent, settings.SpeechProfileLabel, settings.ConsentBypassAll,
            settings.ConsentBypassSources, settings.ConsentBypassDangerousSources, settings.RequireRole), cancellationToken).ConfigureAwait(false);
        _clientAssistant = settings;
    }

    public Cockpit.Core.Abstractions.Layout.ILayoutSettingsStore Layout { get; } = new Layout.LayoutSettingsStore();
    public Cockpit.Core.Abstractions.TranscriptDisplay.ITranscriptDisplaySettingsStore Transcript { get; } = new TranscriptDisplay.TranscriptDisplaySettingsStore();
    public Cockpit.Core.Abstractions.UsagePill.IUsagePillSettingsStore UsagePill { get; } = new UsagePill.UsagePillSettingsStore();
    public Cockpit.Core.Abstractions.Voice.IVoiceSettingsStore Voice { get; } = new Voice.VoiceSettingsStore();
    public Cockpit.Core.Abstractions.Shortcuts.IShortcutSettingsStore Shortcuts { get; } = new Shortcuts.ShortcutSettingsStore();
    public Cockpit.Core.Abstractions.Rendering.IRenderingSettingsStore Rendering { get; } = new Rendering.RenderingSettingsStore();
    public Cockpit.Core.Abstractions.Screenshots.IScreenshotSettingsStore Screenshots { get; } = new Screenshots.ScreenshotSettingsStore();
    public Cockpit.Core.Abstractions.QuickNotes.IQuickNoteSettingsStore QuickNotes { get; } = new QuickNotes.QuickNoteSettingsStore();

    public IReadOnlyList<ShellDescriptor> Shells { get; private set; } = [];

    private RemoteSwarmOptions? _swarm;

    private RemoteSwarmOptions _Swarm => _swarm ??= new(() => Client);

    public ISwarmOptions Swarm => _Swarm;

    public ISwarmLeadership SwarmLeadership => _Swarm;

    private RemoteMcpRegistry? _mcp;

    private RemoteMcpRegistry _Mcp => _mcp ??= new(() => Client, () => ReadConfirmedValues);

    public Cockpit.Core.Abstractions.Mcp.IMcpServerStore McpServers => _Mcp;

    public Cockpit.Core.Abstractions.Mcp.IMcpOAuthCoordinator McpSignIns => _Mcp;

    string ICloneSettingsStore.DefaultRoot => _cloneDefault;
    string IWorktreeSettingsStore.DefaultRoot => _worktreeDefault;

    private async Task<T> _ReadAsync<T>(string group, CancellationToken ct) where T : class
    {
        if (ReadConfirmedValues && _confirmed.TryGetValue(group, out var confirmed))
        {
            return (T)confirmed;
        }

        var value = await Client.SendAsync<T>(HttpMethod.Get, $"api/v1/options/{group}", null, ConnectKeyEndpoints.Json, ct).ConfigureAwait(false);
        _confirmed[group] = value;
        return value;
    }

    private async Task<T> _WriteAsync<T>(string group, T value, CancellationToken ct) where T : class
    {
        var confirmed = await Client.SendAsync<T>(HttpMethod.Put, $"api/v1/options/{group}", value, ConnectKeyEndpoints.Json, ct).ConfigureAwait(false);
        _confirmed[group] = confirmed;
        return confirmed;
    }

    async Task<HeavyRunSettings> IHeavyRunSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var value = await _ReadAsync<RunOptions>("runs", cancellationToken).ConfigureAwait(false);
        return new() { MaxParallelRuns = value.MaxParallelRuns, CpuCeilingPercent = value.CpuCeilingPercent, MemoryCeilingPercent = value.MemoryCeilingPercent };
    }

    Task IHeavyRunSettingsStore.SaveAsync(HeavyRunSettings settings, CancellationToken cancellationToken) =>
        _WriteAsync("runs", new RunOptions(settings.MaxParallelRuns, settings.CpuCeilingPercent, settings.MemoryCeilingPercent), cancellationToken);

    async Task<SessionBehaviorSettings> ISessionBehaviorSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var value = await _ReadAsync<SessionOptions>("sessions", cancellationToken).ConfigureAwait(false);
        var cleanup = ReadConfirmedValues ? _confirmedCleanup : await new RemoteServerStorageSettings(Client).GetAutomaticDockerCleanupAsync(cancellationToken).ConfigureAwait(false);
        _confirmedCleanup = cleanup;
        return new() { AutoCloseOnExit = value.AutoCloseOnExit, CombineQueuedMessages = value.CombineQueuedMessages, WakeAgentsByDefault = value.WakeAgentsByDefault, MemoryBudgetPercent = value.MemoryBudgetPercent, PluginRestartAdviceAfter = value.PluginRestartAdviceAfter, AutomaticDockerCleanup = cleanup };
    }

    async Task ISessionBehaviorSettingsStore.SaveAsync(SessionBehaviorSettings settings, CancellationToken cancellationToken)
    {
        await _WriteAsync("sessions", new SessionOptions(settings.AutoCloseOnExit, settings.CombineQueuedMessages, settings.WakeAgentsByDefault, settings.MemoryBudgetPercent, settings.PluginRestartAdviceAfter), cancellationToken).ConfigureAwait(false);
        await new RemoteServerStorageSettings(Client).SetAutomaticDockerCleanupAsync(settings.AutomaticDockerCleanup, cancellationToken).ConfigureAwait(false);
        _confirmedCleanup = settings.AutomaticDockerCleanup;
    }

    async Task<CloneSettings> ICloneSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var value = await _ReadAsync<RootOptions>("clones", cancellationToken).ConfigureAwait(false);
        _cloneDefault = value.DefaultRoot ?? string.Empty;
        return new() { Root = value.Root };
    }

    Task ICloneSettingsStore.SaveAsync(CloneSettings settings, CancellationToken cancellationToken) => _WriteAsync("clones", new RootOptions(settings.Root, null), cancellationToken);

    async Task<WorktreeSettings> IWorktreeSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var value = await _ReadAsync<RootOptions>("worktrees", cancellationToken).ConfigureAwait(false);
        _worktreeDefault = value.DefaultRoot ?? string.Empty;
        return new() { Root = value.Root };
    }

    Task IWorktreeSettingsStore.SaveAsync(WorktreeSettings settings, CancellationToken cancellationToken) => _WriteAsync("worktrees", new RootOptions(settings.Root, null), cancellationToken);

    async Task<NotificationSettings> INotificationSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var value = await _ReadAsync<NotificationOptions>("notifications", cancellationToken).ConfigureAwait(false);
        _clientNotifications = await _notifications.LoadAsync(cancellationToken).ConfigureAwait(false);
        return _clientNotifications with
        {
            DiscordEnabled = value.DiscordEnabled, WebhookUrl = value.WebhookUrl, IdleThreshold = value.IdleThreshold,
            SessionIdleThreshold = value.SessionIdleThreshold, NotifyOnSessionFinished = value.NotifyOnSessionFinished,
            NotifyOnSessionIdle = value.NotifyOnSessionIdle, NotifyWhenAllSessionsIdle = value.NotifyWhenAllSessionsIdle,
            NotifyOnCiFailure = value.NotifyOnCiFailure, MonitorMessagesPerHour = value.MonitorMessagesPerHour,
        };
    }

    async Task INotificationSettingsStore.SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        await _WriteAsync("notifications", new NotificationOptions(settings.DiscordEnabled, settings.WebhookUrl, settings.IdleThreshold, settings.SessionIdleThreshold, settings.NotifyOnSessionFinished, settings.NotifyOnSessionIdle, settings.NotifyWhenAllSessionsIdle, settings.NotifyOnCiFailure, settings.MonitorMessagesPerHour), cancellationToken).ConfigureAwait(false);
        _clientNotifications = settings;
    }

    async Task<DebugSettings> IDebugSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        _clientDebug = await _debug.LoadAsync(cancellationToken).ConfigureAwait(false);
        return _clientDebug with { LogDiagnosticSnapshots = (await _ReadAsync<DiagnosticOptions>("diagnostics", cancellationToken).ConfigureAwait(false)).LogDiagnosticSnapshots };
    }

    async Task IDebugSettingsStore.SaveAsync(DebugSettings settings, CancellationToken cancellationToken)
    {
        await _WriteAsync("diagnostics", new DiagnosticOptions(settings.LogDiagnosticSnapshots), cancellationToken).ConfigureAwait(false);
        _clientDebug = settings;
    }

    async Task<TerminalSettings> ITerminalSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        _clientTerminal = await _terminal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var value = await _ReadAsync<ShellOptions>("shell", cancellationToken).ConfigureAwait(false);
        Shells = value.Shells ?? [];
        return _clientTerminal with { Shell = value.Shell };
    }

    async Task ITerminalSettingsStore.SaveAsync(TerminalSettings settings, CancellationToken cancellationToken)
    {
        await _WriteAsync("shell", new ShellOptions(settings.Shell), cancellationToken).ConfigureAwait(false);
        _clientTerminal = settings;
    }

    async Task<TerminalAccessSettings> ITerminalAccessSettingsStore.LoadAsync(CancellationToken cancellationToken) =>
        new() { Enabled = (await _ReadAsync<AccessOptions>("terminal-access", cancellationToken).ConfigureAwait(false)).Enabled };

    Task ITerminalAccessSettingsStore.SaveAsync(TerminalAccessSettings settings, CancellationToken cancellationToken) =>
        _WriteAsync("terminal-access", new AccessOptions(settings.Enabled), cancellationToken);

    async Task<ShellAccessSettings> IShellAccessSettingsStore.LoadAsync(CancellationToken cancellationToken) =>
        new() { Enabled = (await _ReadAsync<AccessOptions>("shell-access", cancellationToken).ConfigureAwait(false)).Enabled };

    Task IShellAccessSettingsStore.SaveAsync(ShellAccessSettings settings, CancellationToken cancellationToken) =>
        _WriteAsync("shell-access", new AccessOptions(settings.Enabled), cancellationToken);

    // Only after every server write succeeded. Backend values, including the webhook, never enter client state.
    public async Task SaveClientPresentationAsync(CancellationToken cancellationToken = default)
    {
        var notifications = await _notifications.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _notifications.SaveAsync(notifications with
        {
            LocalEnabled = _clientNotifications.LocalEnabled,
            NotificationSoundsEnabled = _clientNotifications.NotificationSoundsEnabled,
            NotificationSoundOnSessionFinished = _clientNotifications.NotificationSoundOnSessionFinished,
            NotificationSoundOnNeedsAttention = _clientNotifications.NotificationSoundOnNeedsAttention,
            SessionFinishedSound = _clientNotifications.SessionFinishedSound,
            NeedsAttentionSound = _clientNotifications.NeedsAttentionSound,
            SessionFinishedCustomSoundPath = _clientNotifications.SessionFinishedCustomSoundPath,
            NeedsAttentionCustomSoundPath = _clientNotifications.NeedsAttentionCustomSoundPath,
            NotificationSoundVolume = _clientNotifications.NotificationSoundVolume,
        }, cancellationToken).ConfigureAwait(false);
        await _debug.SaveAsync((await _debug.LoadAsync(cancellationToken).ConfigureAwait(false)) with { ShowDebugControls = _clientDebug.ShowDebugControls }, cancellationToken).ConfigureAwait(false);
        await _terminal.SaveAsync((await _terminal.LoadAsync(cancellationToken).ConfigureAwait(false)) with { FontFamily = _clientTerminal.FontFamily, FontSize = _clientTerminal.FontSize }, cancellationToken).ConfigureAwait(false);
        await _assistant.UpdateAsync(stored => stored with
        {
            AlwaysOnTop = _clientAssistant.AlwaysOnTop, PushToTalkKeyName = _clientAssistant.PushToTalkKeyName,
            SpeakReplies = _clientAssistant.SpeakReplies, SpeechMode = _clientAssistant.SpeechMode,
            SpeechMethod = _clientAssistant.SpeechMethod, WakeWordEnabled = _clientAssistant.WakeWordEnabled,
            WakeWord = _clientAssistant.WakeWord, WakeWordListeningSeconds = _clientAssistant.WakeWordListeningSeconds,
        }, cancellationToken).ConfigureAwait(false);
    }
}
