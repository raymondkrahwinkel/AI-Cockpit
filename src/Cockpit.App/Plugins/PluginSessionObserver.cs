using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// The live `ICockpitSessionObserver` backing `ICockpitHost.Sessions`: tracks the cockpit's selected
// session and relays output. One shared instance serves all plugins, mirroring the shared `ICockpitActions`;
// all events are marshalled to the UI thread.
internal sealed class PluginSessionObserver : ICockpitSessionObserver, IPluginActiveSession
{
    private readonly CockpitViewModel _cockpit;
    private ObservableCollection<SessionPanelViewModel> Sessions => _cockpit.IsRemoteWindow ? _cockpit.GridPanes : _cockpit.Sessions;
    private readonly ISessionRegistry? _registry;
    private readonly ISessionLauncher? _launcher;
    private readonly IRemoteServer? _server;
    private ISessionRegistry? Registry => _server?.Sessions ?? _registry;
    private ISessionLauncher? Launcher => _server?.Launcher ?? _launcher;

    // Sessions we have hooked, so we can detach cleanly (no leaked handlers) and avoid double-hooking on a
    // spurious reset. The value is that session's own RateLimits handler, kept to unsubscribe the exact delegate added.
    private readonly Dictionary<SessionPanelViewModel, NotifyCollectionChangedEventHandler> _hooked = [];

    // AC-2256: the registry and launcher serve the transcript and permission reads; without them both read as nothing.
    public PluginSessionObserver(CockpitViewModel cockpit, ISessionRegistry? registry = null, ISessionLauncher? launcher = null, IRemoteServer? server = null)
    {
        _cockpit = cockpit;
        _registry = registry;
        _launcher = launcher;
        _server = server;
        _cockpit.PropertyChanged += _OnCockpitPropertyChanged;
        Sessions.CollectionChanged += _OnSessionsChanged;

        foreach (var session in Sessions)
        {
            _Hook(session);
        }
    }

    public string? ActiveSessionWorkingDirectory => _cockpit.SelectedSession?.WorkingDirectory;

    public string? ActivePaneId => _cockpit.SelectedSession?.PaneId;

    public string? ActiveProjectId => _cockpit.SelectedSession?.ProjectId;

    // The assistant is appended rather than hooked into `Sessions` itself (AC-833): it deliberately sits
    // outside that collection (see `CockpitViewModel.CreateAssistantSession`) so nothing else that iterates
    // Sessions picks it up by accident, but it is still a session an "open sessions" list must be able to name.
    public IReadOnlyList<OpenCockpitSession> OpenSessions =>
        [.. Sessions.Append<SessionPanelViewModel?>(_cockpit.AssistantPane).OfType<SessionPanelViewModel>().Select(_Describe)];

    public SessionUsageSnapshot? ActiveSessionUsage => _Snapshot(_cockpit.SelectedSession);

    public event EventHandler? ActiveSessionChanged;

    public event EventHandler? ActiveSessionUsageChanged;

    public event EventHandler<SessionOutputText>? OutputProduced;

    public event EventHandler<SessionToolActivity>? ToolActivityObserved;

    public event EventHandler<string>? SessionClosed;

    public IReadOnlyList<SessionImageAttachment> GetCurrentTurnImages(string paneId)
    {
        // The auto-attach path calls this on the UI thread, but the fallback MCP tool calls it from the endpoint's
        // request thread — enumerating the sessions ObservableCollection there while the UI thread adds/removes a
        // session would throw. Marshal the read onto the UI thread (inline when already on it).
        IReadOnlyList<SessionImageAttachment> Read() =>
            Sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal))
                ?.CurrentTurnImages ?? [];

        return UiThreadCall.Run(Read);
    }

    // AC-1397: marshalled for the same reason as GetCurrentTurnImages — the MCP tool that asks runs on a request thread.
    public string? GetWorkingDirectory(string paneId)
    {
        string? Read() =>
            Sessions.Append<SessionPanelViewModel?>(_cockpit.AssistantPane)
                .FirstOrDefault(session => session is not null && string.Equals(session.PaneId, paneId, StringComparison.Ordinal))
                ?.WorkingDirectory;

        return UiThreadCall.Run(Read);
    }

    public Task<PluginTranscriptSlice?> ReadTranscriptAsync(string paneId, int count) =>
        Registry is not { } registry ? Task.FromResult<PluginTranscriptSlice?>(null) : PluginSessionReads.ReadTranscriptAsync(registry, Launcher, paneId, count);

    public Task<IReadOnlyList<PluginPendingPermission>> ReadPendingPermissionsAsync(string paneId) =>
        Registry is not { } registry ? Task.FromResult<IReadOnlyList<PluginPendingPermission>>([]) : PluginSessionReads.ReadPendingPermissionsAsync(registry, Launcher, paneId);

    // AC-2256: the same answers SessionPanelHandle gives for these fields, read off the pane the list already walks.
    private static OpenCockpitSession _Describe(SessionPanelViewModel session) =>
        new(session.PaneId, session.Title, session.ActiveProfileLabel, session.SessionStatus.ToString())
        {
            Statusline = string.IsNullOrEmpty(session.Statusline) ? null : session.Statusline,
            IsTerminal = session is TtyViewModel { IsTerminal: true },
            HasReadableTranscript = session switch
            {
                SessionViewModel => true,
                TtyViewModel tty => tty.HasReadableTranscript,
                _ => false,
            },
            AcceptsImages = session is SessionViewModel { CanPasteImages: true },
        };

    // The selected session's ctx/5h/wk as a plugin reads it (AC-54), built from the same fields the header
    // pill renders, carrying the profile label so per-profile history has something to group on. Null when
    // nothing is selected.
    private static SessionUsageSnapshot? _Snapshot(SessionPanelViewModel? session)
    {
        if (session is null)
        {
            return null;
        }

        var windows = session.RateLimits
            .Select(window => new PluginRateLimitWindow(window.Label, window.UsedPercent, window.ResetsAt, WindowMinutes: null))
            .ToList();

        return new SessionUsageSnapshot(session.ActiveProfileLabel, session.ContextUsedPercent, windows);
    }

    private void _OnCockpitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            _RaiseActiveSessionChanged();
            // A new selection is a new usage story, whether or not the working directory moved with it.
            _RaiseActiveSessionUsageChanged();
        }
    }

    private void _OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset (Clear) hands us no OldItems, so reconcile against the live collection instead of guessing.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var session in _hooked.Keys.ToList())
            {
                if (!Sessions.Contains(session))
                {
                    _Unhook(session);
                }
            }

            foreach (var session in Sessions)
            {
                _Hook(session);
            }

            return;
        }

        foreach (var session in e.OldItems?.OfType<SessionPanelViewModel>() ?? [])
        {
            _Unhook(session);
        }

        foreach (var session in e.NewItems?.OfType<SessionPanelViewModel>() ?? [])
        {
            _Hook(session);
        }
    }

    private void _Hook(SessionPanelViewModel session)
    {
        if (_hooked.ContainsKey(session))
        {
            return;
        }

        // A rate window being added/cleared changes the usage snapshot as much as the context percentage does, but
        // it fires on the collection rather than as a property — so watch it too, and relay only while this session
        // is the selected one (a background session's windows do not touch the active-usage surface).
        void OnRateLimitsChanged(object? _, NotifyCollectionChangedEventArgs __)
        {
            if (ReferenceEquals(session, _cockpit.SelectedSession))
            {
                _RaiseActiveSessionUsageChanged();
            }
        }

        _hooked.Add(session, OnRateLimitsChanged);
        session.OutputTextProduced += _OnSessionOutput;
        session.ToolActivityProduced += _OnSessionToolActivity;
        session.PropertyChanged += _OnSessionPropertyChanged;
        session.RateLimits.CollectionChanged += OnRateLimitsChanged;
    }

    private void _Unhook(SessionPanelViewModel session)
    {
        if (!_hooked.Remove(session, out var onRateLimitsChanged))
        {
            return;
        }

        session.OutputTextProduced -= _OnSessionOutput;
        session.ToolActivityProduced -= _OnSessionToolActivity;
        session.PropertyChanged -= _OnSessionPropertyChanged;
        session.RateLimits.CollectionChanged -= onRateLimitsChanged;

        // A session leaving the collection is it closing — the one place a plugin can learn to drop what it kept
        // keyed to that pane. PaneId is still readable on the detached session; capture it before the handler runs.
        var paneId = session.PaneId;
        _OnUiThread(() => SessionClosed?.Invoke(this, paneId));
    }

    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _cockpit.SelectedSession))
        {
            return;
        }

        // The selected session learning its working directory (an SDK session's init event) or its project (AC-1737)
        // is the same "re-scope now" cue as the selection itself changing.
        if (e.PropertyName is nameof(SessionPanelViewModel.WorkingDirectory) or nameof(SessionPanelViewModel.ProjectId))
        {
            _RaiseActiveSessionChanged();
        }

        // The context percentage or the profile label moving is a fresh usage story for the same selection.
        if (e.PropertyName is nameof(SessionPanelViewModel.ContextUsedPercent) or nameof(SessionPanelViewModel.ActiveProfileLabel))
        {
            _RaiseActiveSessionUsageChanged();
        }
    }

    private void _OnSessionOutput(object? sender, string text)
    {
        if (sender is not SessionPanelViewModel session)
        {
            return;
        }

        var payload = new SessionOutputText(
            text,
            session.WorkingDirectory,
            ReferenceEquals(session, _cockpit.SelectedSession));

        _OnUiThread(() => OutputProduced?.Invoke(this, payload));
    }

    private void _OnSessionToolActivity(object? sender, SessionToolActivity activity) =>
        _OnUiThread(() => ToolActivityObserved?.Invoke(this, activity));

    private void _RaiseActiveSessionChanged() =>
        _OnUiThread(() => ActiveSessionChanged?.Invoke(this, EventArgs.Empty));

    private void _RaiseActiveSessionUsageChanged() =>
        _OnUiThread(() => ActiveSessionUsageChanged?.Invoke(this, EventArgs.Empty));

    // Session events can originate off the UI thread (transcript tails, driver event loops); marshal so a
    // plugin handler runs where it can safely touch controls. Already-on-thread stays synchronous.
    private static void _OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
