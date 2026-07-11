using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Audio;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.SessionSwitching;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Multi-instance cockpit shell: owns the collection of running <see cref="ClaudeSessionViewModel"/>
/// panels, which one is selected, and the grid/zoom view mode. Reuses the existing
/// <see cref="ClaudeSessionViewModel"/>/<c>ClaudeSessionView</c> per panel — this view model only
/// adds the manager layer around it. See <c>Memory/Cockpit/Plan.md</c> §Vision-uitbreiding + §UX-eisen.
/// </summary>
/// <remarks>
/// Also carries the F0 audio record/play commands so the sidebar's secondary "Tools" footer (see
/// <c>CockpitView.axaml</c>) can bind to them without reaching into a sibling view model — the
/// cockpit is the single root VM behind the window; audio is a small, secondary tool hanging off it.
/// </remarks>
// Singleton: it is the single root view model behind the window, and the shutdown path resolves it
// back to dispose the live sessions (bug #32) — that must be the same instance the window holds.
public partial class CockpitViewModel : ViewModelBase, ISingletonService, IAsyncDisposable, IPluginContributionSink
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<ClaudeSessionViewModel>? _sessionFactory;
    private readonly Func<ClaudeTtyViewModel>? _ttySessionFactory;
    private readonly ISessionDialogService? _dialogService;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly ISessionSwitchSettingsStore? _sessionSwitchSettingsStore;
    private readonly ITranscriptDisplaySettingsStore? _transcriptDisplaySettingsStore;
    private readonly ISessionBehaviorSettingsStore? _sessionBehaviorSettingsStore;
    private readonly ILayoutSettingsStore? _layoutSettingsStore;
    private readonly IVoiceSettingsStore? _voiceSettingsStore;
    private readonly ITerminalSettingsStore? _terminalSettingsStore;
    private readonly IAudioDeviceProvider? _audioDeviceProvider;
    private readonly PluginDiagnostics? _pluginDiagnostics;
    private readonly List<byte> _recordedPcm = [];

    // Last observed status per session, so a NeedsAttention notification fires only on the edge into
    // that state — not on every property change while a session already needs attention.
    private readonly Dictionary<SessionPanelViewModel, SessionStatus> _lastStatus = [];
    private CancellationTokenSource? _recordingCancellation;
    private int _sessionCounter;

    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

    /// <summary>Left-menu accordion sections contributed by plugins (#14), shown under the session list. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSideSection> PluginSideSections { get; } = [];

    /// <summary>Left-menu launcher buttons contributed by plugins (#14); clicking one runs the plugin's action (typically opening a dialog).</summary>
    public ObservableCollection<PluginSideButton> PluginSideButtons { get; } = [];

    /// <summary>Per-plugin settings views (#14) keyed by plugin folder id, opened from the gear in the plugin manager.</summary>
    public Dictionary<string, Func<Control>> PluginSettings { get; } = [];

    /// <summary>The "Plugins" Options tab (#14): install/enable/disable/remove installed plugins. Loaded when the Options dialog opens.</summary>
    public PluginManagerViewModel Plugins { get; }

    /// <summary>A dismissible banner shown when one or more plugins failed to load (#14) — the app keeps running; details are in Options → Plugins.</summary>
    [ObservableProperty]
    private string _pluginFailureBanner = string.Empty;

    /// <summary>True while the plugin-failure banner should be shown.</summary>
    [ObservableProperty]
    private bool _hasPluginFailures;

    /// <summary>Reads the recorded plugin failures and raises the startup banner; called after plugin phase-2 completes.</summary>
    public void RefreshPluginFailures()
    {
        var failures = _pluginDiagnostics?.Failures ?? [];
        HasPluginFailures = failures.Count > 0;
        PluginFailureBanner = failures.Count switch
        {
            0 => string.Empty,
            1 => $"A plugin failed to load: {failures[0].DisplayName}. See Options → Plugins for details.",
            _ => $"{failures.Count} plugins failed to load. See Options → Plugins for details.",
        };
    }

    [RelayCommand]
    private void DismissPluginFailures() => HasPluginFailures = false;

    void IPluginContributionSink.AddPluginSideSection(string title, Func<Control> createView) =>
        _OnUiThread(() => PluginSideSections.Add(new PluginSideSection(title, createView)));

    void IPluginContributionSink.AddPluginSideButton(string title, Action onInvoke) =>
        _OnUiThread(() => PluginSideButtons.Add(new PluginSideButton(title, onInvoke)));

    void IPluginContributionSink.AddPluginSettings(string pluginId, Func<Control> createView) =>
        _OnUiThread(() => PluginSettings[pluginId] = createView);

    // Plugins register contributions from Initialize (run on the UI thread), but a plugin could also
    // add a section later off a background thread — marshal so the bound collections only mutate on the UI thread.
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

    /// <summary>False when no session is open, driving the empty-state welcome screen vs. the session grid (#31).</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// Column count for the adaptive session grid (#24): one session fills the width; two or more lay
    /// out in two columns (so 3–4 form a 2×2), rather than the old fixed two that left a single session
    /// pinned to the left half.
    /// </summary>
    public int GridColumns => Sessions.Count <= 1 ? 1 : 2;

    /// <summary>The Zoom toggle only makes sense in the grid layout with more than one session — a single session already fills the pane, and single-session layout has no grid to zoom out of.</summary>
    public bool ShowZoomButton => !SingleSessionLayout && Sessions.Count > 1;

    [ObservableProperty]
    private SessionPanelViewModel? _selectedSession;

    /// <summary>True while the grid is collapsed to show only <see cref="SelectedSession"/> at full width.</summary>
    [ObservableProperty]
    private bool _isZoomed;

    /// <summary>
    /// When true, the cockpit always shows one session at a time (single-session layout, #24), switched
    /// from the sidebar — instead of the multi-session grid. Persisted; the Zoom button is a transient
    /// per-view version of the same thing.
    /// </summary>
    [ObservableProperty]
    private bool _singleSessionLayout;

    /// <summary>When true, the multi-session grid stacks panels in one column (one above the other) instead of tiling side by side. Bound to the grid's <see cref="Controls.SessionTilePanel.StackVertically"/>.</summary>
    [ObservableProperty]
    private bool _stackSessionsVertically;

    /// <summary>When true, closing the window hides it to the system tray and keeps the app running (#33). Read by MainWindow on close.</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    [ObservableProperty]
    private string _layoutSettingsStatus = string.Empty;

    /// <summary>
    /// Global TTY terminal font family (#40) — one setting for every TTY session, not per-profile or
    /// per-session. Fed straight into <c>TerminalControl.FontFamily</c>, so both a single family name and
    /// a comma-separated fallback list work; the curated <see cref="TerminalFontFamilies"/> list offers
    /// common choices but the field stays free text.
    /// </summary>
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    /// <summary>Global TTY terminal font size in points (#40), clamped to <see cref="Cockpit.Core.Terminal.TerminalSettings.MinFontSize"/>-<see cref="Cockpit.Core.Terminal.TerminalSettings.MaxFontSize"/> on save.</summary>
    [ObservableProperty]
    private int _terminalFontSize = 13;

    [ObservableProperty]
    private string _terminalSettingsStatus = string.Empty;

    /// <summary>Curated monospace font choices offered by the Options dialog's editable Terminal font-family box — the field also accepts free text for any font installed locally.</summary>
    public IReadOnlyList<string> TerminalFontFamilies { get; } =
    [
        "Cascadia Mono, Consolas, monospace",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "DejaVu Sans Mono",
        "Courier New",
    ];

    /// <summary>Pushes the terminal font family to every open TTY session as it changes (#40), so Options → Terminal applies live without a restart.</summary>
    partial void OnTerminalFontFamilyChanged(string value)
    {
        foreach (var session in Sessions)
        {
            if (session is ClaudeTtyViewModel tty)
            {
                tty.TerminalFontFamily = value;
            }
        }
    }

    /// <summary>Pushes the terminal font size to every open TTY session as it changes (#40), same live-apply as <see cref="OnTerminalFontFamilyChanged"/>.</summary>
    partial void OnTerminalFontSizeChanged(int value)
    {
        foreach (var session in Sessions)
        {
            if (session is ClaudeTtyViewModel tty)
            {
                tty.TerminalFontSize = value;
            }
        }
    }

    /// <summary>True when only the selected session should be shown full-size — either the persisted single layout (#24) or a transient Zoom.</summary>
    public bool ShowSinglePane => SingleSessionLayout || IsZoomed;

    partial void OnIsZoomedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        RefreshPaneVisibility();
    }

    partial void OnSingleSessionLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(ShowZoomButton));
        RefreshPaneVisibility();
    }

    [ObservableProperty]
    private string _audioStatus = "Ready.";

    /// <summary>Whether a local OS toast is shown when a session needs attention while you are present (independent of Discord).</summary>
    [ObservableProperty]
    private bool _localNotificationsEnabled = true;

    /// <summary>Whether the Discord webhook is POSTed when a session needs attention while you are away (independent of local toasts).</summary>
    [ObservableProperty]
    private bool _discordNotificationsEnabled;

    /// <summary>Discord webhook URL POSTed to when the operator is away. Empty disables the away channel.</summary>
    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    /// <summary>Idle minutes before the operator counts as "away" (when the PC is not locked).</summary>
    [ObservableProperty]
    private int _idleThresholdMinutes = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    [ObservableProperty]
    private string _notificationSettingsStatus = string.Empty;

    /// <summary>One shared "Saved" indicator for the Options dialog's single footer Save (#13), shown next to the Save button instead of a per-section label.</summary>
    [ObservableProperty]
    private string _allSettingsStatus = string.Empty;

    /// <summary>Master switch for the arrow-key session switch (Ctrl+Arrow by default).</summary>
    [ObservableProperty]
    private bool _sessionSwitchEnabled = true;

    /// <summary>The modifier that arms the arrow-key session switch. See <see cref="SessionSwitchModifier"/>.</summary>
    [ObservableProperty]
    private SessionSwitchModifierOption _selectedSessionSwitchModifier =
        new("Ctrl", SessionSwitchModifier.Ctrl);

    [ObservableProperty]
    private string _sessionSwitchSettingsStatus = string.Empty;

    /// <summary>When true, every transcript row shows its arrival timestamp (T7). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _showTimestamps;

    [ObservableProperty]
    private string _transcriptDisplaySettingsStatus = string.Empty;

    /// <summary>When true, sending "exit" closes the session after its turn completes (T10). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

    [ObservableProperty]
    private string _sessionBehaviorSettingsStatus = string.Empty;

    /// <summary>Master switch for voice input (push-to-talk dictation). Off by default — enabling it is what triggers the first Whisper model download.</summary>
    [ObservableProperty]
    private bool _voiceEnabled;

    /// <summary>Ggml model name, e.g. "large-v3-turbo", "base", "tiny" — smaller models download faster and transcribe faster on CPU-only hardware.</summary>
    [ObservableProperty]
    private string _voiceModelName = "large-v3-turbo";

    /// <summary>Selectable Whisper backend preferences offered by the Options flyout combo box.</summary>
    public IReadOnlyList<VoiceBackendPreferenceOption> VoiceBackendPreferences { get; } =
    [
        new("Auto (best available)", VoiceBackendPreference.Auto),
        new("CUDA (NVIDIA)", VoiceBackendPreference.Cuda),
        new("Vulkan (Windows only)", VoiceBackendPreference.Vulkan),
        new("CPU", VoiceBackendPreference.Cpu),
    ];

    [ObservableProperty]
    private VoiceBackendPreferenceOption _selectedVoiceBackendPreference = new("Auto (best available)", VoiceBackendPreference.Auto);

    /// <summary>Selectable dictation languages for speech-to-text — "Auto-detect" plus common fixed languages. A fixed language beats detection when you always dictate in one tongue (Options flyout combo).</summary>
    public IReadOnlyList<SttLanguageOption> SttLanguages { get; } =
    [
        new("Auto-detect", "auto"),
        new("Dutch", "nl"),
        new("English", "en"),
        new("German", "de"),
        new("French", "fr"),
        new("Spanish", "es"),
    ];

    [ObservableProperty]
    private SttLanguageOption _selectedSttLanguage = new("Auto-detect", "auto");

    /// <summary>Input (microphone) devices offered by the Options combo box; the first entry is the system default. Refreshed from the audio backend when the voice settings load.</summary>
    public ObservableCollection<AudioDeviceOption> InputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedInputDevice = new("System default", null);

    /// <summary>Output (playback) devices for read-aloud (#35); the first entry is the system default.</summary>
    public ObservableCollection<AudioDeviceOption> OutputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedOutputDevice = new("System default", null);

    /// <summary>Whether a transcript is passed through the local Ollama cleanup step before injection.</summary>
    [ObservableProperty]
    private bool _voiceCleanupEnabled = true;

    /// <summary>Ollama model tag used for the cleanup step (see <see cref="VoiceCleanupEnabled"/>).</summary>
    [ObservableProperty]
    private string _voiceCleanupModel = "qwen2.5:3b-instruct";

    /// <summary>Base URL of the local Ollama daemon used for cleanup.</summary>
    [ObservableProperty]
    private string _voiceOllamaBaseUrl = "http://localhost:11434";

    /// <summary>Avalonia <c>Key</c> enum name for the push-to-talk hotkey, e.g. "F9".</summary>
    [ObservableProperty]
    private string _voicePushToTalkKeyName = "F9";

    /// <summary>
    /// When true, the push-to-talk hotkey also fires while the cockpit window has no focus (#34), via
    /// <c>VoicePushToTalkCoordinator</c>. Off by default — opt-in like voice itself.
    /// </summary>
    [ObservableProperty]
    private bool _voiceGlobalPushToTalk;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoSubmitAfterVoice"/>. When true a finished transcript is submitted straight after injection instead of waiting for a manual send. Off by default.</summary>
    [ObservableProperty]
    private bool _voiceAutoSubmit;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.OpenMicSilenceTimeoutMs"/>: trailing silence (ms) that ends an open-mic utterance. Tunable.</summary>
    [ObservableProperty]
    private int _voiceOpenMicSilenceTimeoutMs = 800;

    /// <summary>The open-mic coordinator, wired at startup, exposing the runtime on/off toggle bound to the sidebar mic button (open-mic is turned on/off live, not via a settings checkbox).</summary>
    [ObservableProperty]
    private OpenMicCoordinator? _openMic;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.NaturalizeReadAloud"/>: rewrite read-aloud text into natural speech via the local LLM before synthesis (#35). Off by default.</summary>
    [ObservableProperty]
    private bool _voiceNaturalizeReadAloud;

    /// <summary>Selectable read-aloud voices (#35) offered by the Options flyout combo box.</summary>
    public IReadOnlyList<PiperVoiceOption> TtsVoices => PiperVoiceCatalog.Voices;

    /// <summary>Piper voice used for read-aloud (#35). The model downloads lazily on first use, the same as the Whisper model.</summary>
    [ObservableProperty]
    private PiperVoiceOption _selectedTtsVoice = PiperVoiceCatalog.Default;

    /// <summary>Piper voice the Dutch segments of a mixed-language read-aloud reply route to when naturalization tags the languages (#35). Drawn from the same <see cref="TtsVoices"/> list.</summary>
    [ObservableProperty]
    private PiperVoiceOption _selectedDutchTtsVoice = PiperVoiceCatalog.DutchDefault;

    [ObservableProperty]
    private string _voiceSettingsStatus = string.Empty;

    /// <summary>
    /// True on Linux, where the physical key for global push-to-talk is bound by the desktop's own
    /// Shortcuts settings rather than configurable in-app (#34) — drives the Options-flyout hint text.
    /// </summary>
    public bool IsLinuxPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Pushes the timestamp toggle to every open session as it changes, so the switch takes effect live.</summary>
    partial void OnShowTimestampsChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.ShowTimestamps = value;
        }
    }

    /// <summary>Pushes the auto-close-on-exit toggle to every open session as it changes.</summary>
    partial void OnAutoCloseOnExitChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.AutoCloseOnExit = value;
        }
    }

    /// <summary>Selectable modifiers for the session-switch gesture (bound by the Options flyout combo box).</summary>
    public IReadOnlyList<SessionSwitchModifierOption> SessionSwitchModifiers { get; } =
    [
        new("Ctrl", SessionSwitchModifier.Ctrl),
        new("Ctrl + Alt", SessionSwitchModifier.CtrlAlt),
        new("Alt", SessionSwitchModifier.Alt),
    ];

    /// <summary>
    /// The current switch settings as the view needs them for its KeyDown gate: whether the gesture is
    /// enabled and which modifier arms it. Reflects the live (possibly unsaved) Options-flyout edits.
    /// </summary>
    public SessionSwitchSettings CurrentSessionSwitchSettings => new()
    {
        IsEnabled = SessionSwitchEnabled,
        Modifier = SelectedSessionSwitchModifier.Value,
    };

    /// <summary>Keeps each session's <see cref="ClaudeSessionViewModel.IsSelected"/> in sync with the active selection.</summary>
    partial void OnSelectedSessionChanged(SessionPanelViewModel? oldValue, SessionPanelViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        RefreshPaneVisibility();
    }

    /// <summary>
    /// Sets each session's <see cref="SessionPanelViewModel.IsPaneVisible"/> for the current layout: all
    /// visible in the multi-session grid, only the selected one in single-pane mode (#24 / Zoom). Driven
    /// from C# on every selection/layout change rather than a per-item XAML binding, so the one live grid
    /// reliably shows exactly one panel in single-pane mode instead of stacking them.
    /// </summary>
    private void RefreshPaneVisibility()
    {
        var single = ShowSinglePane;
        foreach (var session in Sessions)
        {
            session.IsPaneVisible = !single || session.IsSelected;
        }
    }

    // Parameterless constructor kept for the Avalonia previewer/Screenshotter design-time context —
    // seeds three sample sessions with different statuses/profiles so the render shows the overview
    // + grid without a real DI-backed session behind each one.
    public CockpitViewModel()
    {
        var waiting = new ClaudeSessionViewModel { Title = "Claude 1", ActiveProfileLabel = "work", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new ClaudeSessionViewModel { Title = "Claude 2", ActiveProfileLabel = "private", SessionStatus = SessionStatus.Busy };
        var tty = new ClaudeTtyViewModel { Title = "Claude 3", ActiveProfileLabel = "work (TTY)", SessionStatus = SessionStatus.Busy };

        Sessions.Add(waiting);
        Sessions.Add(busy);
        Sessions.Add(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
        Plugins = new PluginManagerViewModel();
    }

    public CockpitViewModel(
        Func<ClaudeSessionViewModel> sessionFactory,
        Func<ClaudeTtyViewModel> ttySessionFactory,
        ISessionDialogService dialogService,
        IAudioCaptureService captureService,
        IAudioPlaybackService playbackService,
        IAttentionNotifier attentionNotifier,
        INotificationSettingsStore notificationSettingsStore,
        ISessionSwitchSettingsStore sessionSwitchSettingsStore,
        ITranscriptDisplaySettingsStore transcriptDisplaySettingsStore,
        ISessionBehaviorSettingsStore sessionBehaviorSettingsStore,
        ILayoutSettingsStore layoutSettingsStore,
        IVoiceSettingsStore voiceSettingsStore,
        ITerminalSettingsStore terminalSettingsStore,
        IPluginRegistrationStore? pluginRegistrationStore = null,
        IPluginInstaller? pluginInstaller = null,
        PluginBootstrap? pluginBootstrap = null,
        IPluginStoreConfigStore? pluginStoreConfigStore = null,
        IPluginStoreClient? pluginStoreClient = null,
        IPluginDialogHost? pluginDialogHost = null,
        PluginDiagnostics? pluginDiagnostics = null,
        IAudioDeviceProvider? audioDeviceProvider = null)
    {
        _audioDeviceProvider = audioDeviceProvider;
        _pluginDiagnostics = pluginDiagnostics;
        // The full plugin manager needs its store/installer/bootstrap, store dependencies, the dialog host
        // and the diagnostics; when they are absent (unit tests that don't exercise plugins) the design-time
        // manager is used, so the tab is inert.
        Plugins = pluginRegistrationStore is not null && pluginInstaller is not null && pluginBootstrap is not null
                && pluginStoreConfigStore is not null && pluginStoreClient is not null && pluginDialogHost is not null
                && pluginDiagnostics is not null
            ? new PluginManagerViewModel(pluginRegistrationStore, pluginInstaller, pluginBootstrap, dialogService, pluginStoreConfigStore, pluginStoreClient, PluginSettings, pluginDialogHost, pluginDiagnostics)
            : new PluginManagerViewModel();
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _dialogService = dialogService;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
        _sessionSwitchSettingsStore = sessionSwitchSettingsStore;
        _transcriptDisplaySettingsStore = transcriptDisplaySettingsStore;
        _sessionBehaviorSettingsStore = sessionBehaviorSettingsStore;
        _layoutSettingsStore = layoutSettingsStore;
        _voiceSettingsStore = voiceSettingsStore;
        _terminalSettingsStore = terminalSettingsStore;
        // No session is opened on startup (#31): the app starts on the empty state and a session only
        // exists once the operator creates one from the New-session dialog.
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(GridColumns));
            OnPropertyChanged(nameof(ShowZoomButton));
            RefreshPaneVisibility();
        };
        _ = LoadNotificationSettingsAsync();
        _ = LoadSessionSwitchSettingsAsync();
        _ = LoadTranscriptDisplaySettingsAsync();
        _ = LoadSessionBehaviorSettingsAsync();
        _ = LoadLayoutSettingsAsync();
        _ = LoadVoiceSettingsAsync();
        _ = LoadTerminalSettingsAsync();
    }

    private async Task LoadNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var settings = await _notificationSettingsStore.LoadAsync();
        LocalNotificationsEnabled = settings.LocalEnabled;
        DiscordNotificationsEnabled = settings.DiscordEnabled;
        WebhookUrl = settings.WebhookUrl ?? string.Empty;
        IdleThresholdMinutes = (int)settings.IdleThreshold.TotalMinutes;
    }

    /// <summary>Persists the notification settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var minutes = IdleThresholdMinutes > 0
            ? IdleThresholdMinutes
            : (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

        var settings = new NotificationSettings
        {
            LocalEnabled = LocalNotificationsEnabled,
            DiscordEnabled = DiscordNotificationsEnabled,
            WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
            IdleThreshold = TimeSpan.FromMinutes(minutes),
        };

        await _notificationSettingsStore.SaveAsync(settings);
        NotificationSettingsStatus = "✓ Saved";
    }

    private async Task LoadSessionSwitchSettingsAsync()
    {
        if (_sessionSwitchSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionSwitchSettingsStore.LoadAsync();
        SessionSwitchEnabled = settings.IsEnabled;
        SelectedSessionSwitchModifier = SessionSwitchModifiers.FirstOrDefault(option => option.Value == settings.Modifier)
                                        ?? SessionSwitchModifiers[0];
    }

    /// <summary>Persists the session-switch settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionSwitchSettingsAsync()
    {
        if (_sessionSwitchSettingsStore is null)
        {
            return;
        }

        await _sessionSwitchSettingsStore.SaveAsync(CurrentSessionSwitchSettings);
        SessionSwitchSettingsStatus = "✓ Saved";
    }

    private async Task LoadTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        var settings = await _transcriptDisplaySettingsStore.LoadAsync();
        ShowTimestamps = settings.ShowTimestamps;
    }

    /// <summary>Persists the transcript-display settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        await _transcriptDisplaySettingsStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = ShowTimestamps });
        TranscriptDisplaySettingsStatus = "✓ Saved";
    }

    private async Task LoadSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionBehaviorSettingsStore.LoadAsync();
        AutoCloseOnExit = settings.AutoCloseOnExit;
    }

    /// <summary>Persists the session-behaviour settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        await _sessionBehaviorSettingsStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = AutoCloseOnExit });
        SessionBehaviorSettingsStatus = "✓ Saved";
    }

    private async Task LoadLayoutSettingsAsync()
    {
        if (_layoutSettingsStore is null)
        {
            return;
        }

        var settings = await _layoutSettingsStore.LoadAsync();
        SingleSessionLayout = settings.SingleSessionLayout;
        StackSessionsVertically = settings.StackSessionsVertically;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
    }

    /// <summary>Persists the layout settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveLayoutSettingsAsync()
    {
        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(new LayoutSettings
        {
            SingleSessionLayout = SingleSessionLayout,
            StackSessionsVertically = StackSessionsVertically,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        });
        LayoutSettingsStatus = "✓ Saved";
    }

    private async Task LoadTerminalSettingsAsync()
    {
        if (_terminalSettingsStore is null)
        {
            return;
        }

        var settings = await _terminalSettingsStore.LoadAsync();
        TerminalFontFamily = settings.FontFamily;
        TerminalFontSize = settings.FontSize;
    }

    /// <summary>Persists the TTY terminal-appearance settings (#40) edited in the Options dialog to <c>cockpit.json</c>, clamping the font size to the supported range.</summary>
    [RelayCommand]
    private async Task SaveTerminalSettingsAsync()
    {
        if (_terminalSettingsStore is null)
        {
            return;
        }

        var fontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily)
            ? "Cascadia Mono, Consolas, monospace"
            : TerminalFontFamily.Trim();
        var fontSize = Math.Clamp(TerminalFontSize, TerminalSettings.MinFontSize, TerminalSettings.MaxFontSize);

        await _terminalSettingsStore.SaveAsync(new TerminalSettings { FontFamily = fontFamily, FontSize = fontSize });
        TerminalFontFamily = fontFamily;
        TerminalFontSize = fontSize;
        TerminalSettingsStatus = "✓ Saved";
    }

    private async Task LoadVoiceSettingsAsync()
    {
        if (_voiceSettingsStore is null)
        {
            return;
        }

        var settings = await _voiceSettingsStore.LoadAsync();
        VoiceEnabled = settings.IsEnabled;
        VoiceModelName = settings.ModelName;
        SelectedVoiceBackendPreference = VoiceBackendPreferences.FirstOrDefault(option => option.Value == settings.BackendPreference)
                                         ?? VoiceBackendPreferences[0];
        VoiceCleanupEnabled = settings.CleanupEnabled;
        VoiceCleanupModel = settings.CleanupModel;
        VoiceOllamaBaseUrl = settings.OllamaBaseUrl;
        VoicePushToTalkKeyName = settings.PushToTalkKeyName;
        VoiceGlobalPushToTalk = settings.GlobalPushToTalk;
        VoiceAutoSubmit = settings.AutoSubmitAfterVoice;
        VoiceOpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs;
        VoiceNaturalizeReadAloud = settings.NaturalizeReadAloud;
        SelectedTtsVoice = TtsVoices.FirstOrDefault(voice => voice.VoiceId == settings.TtsVoiceId) ?? PiperVoiceCatalog.Default;
        SelectedDutchTtsVoice = TtsVoices.FirstOrDefault(voice => voice.VoiceId == settings.TtsVoiceIdDutch) ?? PiperVoiceCatalog.DutchDefault;
        SelectedSttLanguage = SttLanguages.FirstOrDefault(language => language.Code == settings.SttLanguage) ?? SttLanguages[0];
    }

    // Re-queries the audio backend so a freshly plugged-in device appears, keeping a "System default"
    // entry at the top, and reselects the saved device. Called when the Options dialog opens rather than
    // at startup: enumerating devices spins up the audio backend, which we only want to touch once the
    // operator actually goes to change it — not on every launch. No-op without a provider (previewer).
    private async Task _RefreshAudioDevicesAsync()
    {
        if (_audioDeviceProvider is null || _voiceSettingsStore is null)
        {
            return;
        }

        var settings = await _voiceSettingsStore.LoadAsync();
        // Enumerating spins up the native audio backend, which can block briefly on first use — run it off
        // the UI thread; the await resumes on the UI thread (captured context) to touch the collections.
        var provider = _audioDeviceProvider;
        var inputDevices = await Task.Run(provider.GetInputDevices);
        var outputDevices = await Task.Run(provider.GetOutputDevices);
        _PopulateDevices(InputDevices, inputDevices);
        _PopulateDevices(OutputDevices, outputDevices);
        SelectedInputDevice = InputDevices.FirstOrDefault(device => device.DeviceName == _NullIfEmpty(settings.InputDeviceName)) ?? InputDevices[0];
        SelectedOutputDevice = OutputDevices.FirstOrDefault(device => device.DeviceName == _NullIfEmpty(settings.OutputDeviceName)) ?? OutputDevices[0];
    }

    private static void _PopulateDevices(ObservableCollection<AudioDeviceOption> target, IReadOnlyList<AudioDeviceInfo> devices)
    {
        target.Clear();
        target.Add(new AudioDeviceOption("System default", null));
        foreach (var device in devices)
        {
            var label = device.IsSystemDefault ? $"{device.Name} (default)" : device.Name;
            target.Add(new AudioDeviceOption(label, device.Name));
        }
    }

    private static string? _NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Persists the voice settings edited in the Options flyout to <c>cockpit.json</c>. Open sessions
    /// re-read the setting the next time they start a push-to-talk hold — no live-push needed, since
    /// <see cref="SessionPanelViewModel.BeginVoiceHold"/> only gates on the enabled flag it loaded once
    /// at session creation, the same "settings apply to new sessions" behaviour as the profile picker.
    /// </summary>
    [RelayCommand]
    private async Task SaveVoiceSettingsAsync()
    {
        if (_voiceSettingsStore is null)
        {
            return;
        }

        // Open-mic on/off is owned by the runtime toggle button, not this dialog — preserve its current
        // persisted value so saving the Options never flips the mic off behind the operator's back.
        var current = await _voiceSettingsStore.LoadAsync();

        await _voiceSettingsStore.SaveAsync(new VoiceSettings
        {
            IsEnabled = VoiceEnabled,
            ModelName = string.IsNullOrWhiteSpace(VoiceModelName) ? "large-v3-turbo" : VoiceModelName.Trim(),
            BackendPreference = SelectedVoiceBackendPreference.Value,
            CleanupEnabled = VoiceCleanupEnabled,
            CleanupModel = string.IsNullOrWhiteSpace(VoiceCleanupModel) ? "qwen2.5:3b-instruct" : VoiceCleanupModel.Trim(),
            OllamaBaseUrl = string.IsNullOrWhiteSpace(VoiceOllamaBaseUrl) ? "http://localhost:11434" : VoiceOllamaBaseUrl.Trim(),
            PushToTalkKeyName = string.IsNullOrWhiteSpace(VoicePushToTalkKeyName) ? "F9" : VoicePushToTalkKeyName.Trim(),
            GlobalPushToTalk = VoiceGlobalPushToTalk,
            AutoSubmitAfterVoice = VoiceAutoSubmit,
            OpenMicEnabled = current.OpenMicEnabled,
            OpenMicSilenceTimeoutMs = VoiceOpenMicSilenceTimeoutMs > 0 ? VoiceOpenMicSilenceTimeoutMs : 800,
            NaturalizeReadAloud = VoiceNaturalizeReadAloud,
            TtsVoiceId = SelectedTtsVoice.VoiceId,
            TtsVoiceIdDutch = SelectedDutchTtsVoice.VoiceId,
            SttLanguage = SelectedSttLanguage.Code,
            InputDeviceName = SelectedInputDevice.DeviceName ?? "",
            OutputDeviceName = SelectedOutputDevice.DeviceName ?? "",
        });

        // Push the read-aloud settings to already-open sessions so toggling naturalization or the voice
        // takes effect immediately, rather than only on the next session (the enabled/PTT flags keep the
        // load-at-start behaviour, which the hold path re-reads).
        foreach (var session in Sessions)
        {
            session.NaturalizeReadAloud = VoiceNaturalizeReadAloud;
            session.TtsVoiceId = SelectedTtsVoice.VoiceId;
            session.DutchTtsVoiceId = SelectedDutchTtsVoice.VoiceId;
        }

        VoiceSettingsStatus = "✓ Saved";
    }

    [RelayCommand]
    private async Task RecordAudioAsync()
    {
        if (_captureService is null)
        {
            return;
        }

        _recordedPcm.Clear();
        _recordingCancellation = new CancellationTokenSource();
        AudioStatus = "Recording...";

        try
        {
            await foreach (var frame in _captureService.CaptureAsync(AudioFormat, _recordingCancellation.Token))
            {
                _recordedPcm.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when StopRecordingAudio cancels the capture stream.
        }

        AudioStatus = $"Recorded {_recordedPcm.Count} bytes.";
    }

    [RelayCommand]
    private void StopRecordingAudio()
    {
        _recordingCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task PlayAudioAsync()
    {
        if (_playbackService is null || _recordedPcm.Count == 0)
        {
            AudioStatus = "Nothing recorded yet.";
            return;
        }

        AudioStatus = "Playing...";
        await _playbackService.PlayAsync(_recordedPcm.ToArray(), AudioFormat);
        AudioStatus = "Playback done.";
    }

    /// <summary>
    /// Opens the New-session dialog — SDK vs TTY is now chosen inside it (#32) — and, once confirmed,
    /// mints the matching session: SDK (headless stream-json rendered as the chat UI) or TTY (the real
    /// interactive <c>claude</c> TUI in a terminal panel, the #9 experiment), started immediately with
    /// the chosen profile and start options.
    /// </summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_sessionFactory is null || _ttySessionFactory is null || _dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync();
        if (result is null)
        {
            return;
        }

        await _LaunchSessionFromResultAsync(result);
    }

    // Mints and starts the matching session (SDK chat or TTY terminal) from a confirmed result, recording
    // the result on the panel so the context-menu Duplicate can replay it.
    private async Task _LaunchSessionFromResultAsync(NewSessionResult result)
    {
        if (_sessionFactory is null || _ttySessionFactory is null)
        {
            return;
        }

        if (result.Kind == SessionKind.Sdk)
        {
            var session = _sessionFactory();
            session.LaunchResult = result;
            AddSession(session, result.SessionName, result.Profile.Label);
            await session.StartConfiguredAsync(result.Profile, result.Mode, result.Model, result.Effort);
        }
        else
        {
            var session = _ttySessionFactory();
            session.LaunchResult = result;
            AddSession(session, result.SessionName, result.Profile.Label);
            session.LaunchConfigured(result.Profile, result.Mode.Value, result.Model.Value, result.Effort.Value);
        }
    }

    /// <summary>Context-menu Rename: begin the sidebar row's inline rename.</summary>
    [RelayCommand]
    private void RenameSession(SessionPanelViewModel session) => session.BeginRename();

    /// <summary>Context-menu Move up: shift the session one place earlier in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionUp(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index > 0)
        {
            Sessions.Move(index, index - 1);
        }
    }

    /// <summary>Context-menu Move down: shift the session one place later in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionDown(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index >= 0 && index < Sessions.Count - 1)
        {
            Sessions.Move(index, index + 1);
        }
    }

    /// <summary>Context-menu Duplicate: start a new session with the same profile/model/mode as this one (≈ Fork).</summary>
    [RelayCommand]
    private async Task DuplicateSessionAsync(SessionPanelViewModel session)
    {
        if (session.LaunchResult is { } result)
        {
            await _LaunchSessionFromResultAsync(result with { SessionName = $"{session.Title} (copy)" });
        }
    }

    /// <summary>Opens the Manage-profiles dialog from the sidebar, independent of creating a session (L2).</summary>
    [RelayCommand]
    private async Task ManageProfilesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowManageProfilesDialogAsync();
    }

    /// <summary>Opens the MCP-servers dialog (#26) from the sidebar to edit the shared MCP-server registry.</summary>
    [RelayCommand]
    private async Task OpenMcpServersAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowMcpServersDialogAsync();
    }

    /// <summary>Opens the Options dialog (#13) from the sidebar, passing this view model as its DataContext.</summary>
    [RelayCommand]
    private async Task OptionsAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _RefreshAudioDevicesAsync();
        await Plugins.LoadAsync();
        await _dialogService.ShowOptionsDialogAsync(this);
    }

    /// <summary>
    /// Persists every options section in one go — the Options dialog's single footer Save (#13)
    /// replaces the six per-section Save buttons the flyout used to have.
    /// </summary>
    [RelayCommand]
    private async Task SaveAllSettingsAsync()
    {
        await SaveNotificationSettingsCommand.ExecuteAsync(null);
        await SaveSessionSwitchSettingsCommand.ExecuteAsync(null);
        await SaveTranscriptDisplaySettingsCommand.ExecuteAsync(null);
        await SaveSessionBehaviorSettingsCommand.ExecuteAsync(null);
        await SaveLayoutSettingsCommand.ExecuteAsync(null);
        await SaveVoiceSettingsCommand.ExecuteAsync(null);
        await SaveTerminalSettingsCommand.ExecuteAsync(null);
        AllSettingsStatus = "✓ Saved";
    }

    private void AddSession(SessionPanelViewModel session, string? name, string profileLabel)
    {
        _sessionCounter++;
        // A friendly name from the dialog wins; otherwise fall back to "<profile> - <N>" so the sidebar
        // shows which profile each session runs under rather than a bare "Claude N".
        session.Title = string.IsNullOrWhiteSpace(name) ? $"{profileLabel} - {_sessionCounter}" : name.Trim();
        // Start the session on the current transcript-display preference; OnShowTimestampsChanged keeps
        // it live afterwards (T7).
        session.ShowTimestamps = ShowTimestamps;
        // Same for the auto-close-on-exit behaviour (T10); the session raises CloseRequested when an
        // "exit" turn completes and the cockpit runs its normal close flow.
        session.AutoCloseOnExit = AutoCloseOnExit;
        // Seed a TTY session with the current global terminal-appearance preference (#40); further
        // changes reach it live via OnTerminalFontFamilyChanged/OnTerminalFontSizeChanged. No effect on
        // SDK sessions — the setting is TTY-only.
        if (session is ClaudeTtyViewModel tty)
        {
            tty.TerminalFontFamily = TerminalFontFamily;
            tty.TerminalFontSize = TerminalFontSize;
        }

        session.CloseRequested += OnSessionCloseRequested;

        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;

        Sessions.Add(session);
        SelectedSession = session;
    }

    /// <summary>
    /// Edge-triggered attention routing: fires the presence-aware notifier once, on the transition
    /// into <see cref="SessionStatus.NeedsAttention"/> — not on every status touch while it stays
    /// there. The notifier itself decides present-toast vs away-webhook.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionPanelViewModel.SessionStatus) || sender is not SessionPanelViewModel session)
        {
            return;
        }

        var previous = _lastStatus.GetValueOrDefault(session, SessionStatus.Idle);
        _lastStatus[session] = session.SessionStatus;

        if (session.SessionStatus == SessionStatus.NeedsAttention && previous != SessionStatus.NeedsAttention)
        {
            NotifyAttention(session);
        }
    }

    /// <summary>A session asked to close itself (T10: an "exit" turn finished) — run the normal close flow.</summary>
    private void OnSessionCloseRequested(object? sender, EventArgs e)
    {
        if (sender is SessionPanelViewModel session)
        {
            _ = CloseSessionAsync(session);
        }
    }

    private void NotifyAttention(SessionPanelViewModel session)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var notification = new AttentionNotification(session.Title, session.SessionStatusLabel);
        // Fire-and-forget: notification delivery must not block the UI thread that raised the status
        // change. The notifier swallows and logs its own transport failures.
        _ = _attentionNotifier.NotifyAttentionAsync(notification);
    }

    [RelayCommand]
    private void SelectSession(SessionPanelViewModel session)
    {
        SelectedSession = session;
    }

    /// <summary>
    /// Moves the selection to the previous session in <see cref="Sessions"/>, wrapping from the first
    /// to the last. No-op when there are no sessions; selects the only session when there is exactly
    /// one. Drives the Ctrl+Left/Ctrl+Up keyboard switch — the modifier/enable gate lives in the view.
    /// </summary>
    public void SelectPreviousSession() => _StepSelection(-1);

    /// <summary>
    /// Moves the selection to the next session in <see cref="Sessions"/>, wrapping from the last to
    /// the first. No-op when there are no sessions. Drives the Ctrl+Right/Ctrl+Down keyboard switch.
    /// </summary>
    public void SelectNextSession() => _StepSelection(1);

    private void _StepSelection(int direction)
    {
        var count = Sessions.Count;
        if (count == 0)
        {
            return;
        }

        // No current selection → land on the first (next) or last (previous) session.
        var currentIndex = SelectedSession is null ? -1 : Sessions.IndexOf(SelectedSession);
        var startIndex = currentIndex < 0 ? (direction > 0 ? -1 : 0) : currentIndex;

        var nextIndex = ((startIndex + direction) % count + count) % count;
        SelectedSession = Sessions[nextIndex];
    }

    [RelayCommand]
    private async Task CloseSessionAsync(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        session.PropertyChanged -= OnSessionPropertyChanged;
        session.CloseRequested -= OnSessionCloseRequested;
        _lastStatus.Remove(session);

        Sessions.RemoveAt(index);
        await session.DisposeAsync();

        if (ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.Count == 0
                ? null
                : Sessions[Math.Min(index, Sessions.Count - 1)];
        }

        if (Sessions.Count == 0)
        {
            IsZoomed = false;
        }
    }

    /// <summary>
    /// Close affordance entry point (#11): a busy session flips its sidebar row to an inline Close/Keep
    /// prompt first, so a running turn is never killed on a single click; an idle/waiting/done session
    /// closes straight away.
    /// </summary>
    [RelayCommand]
    private async Task RequestCloseSessionAsync(SessionPanelViewModel session)
    {
        if (session.RequiresCloseConfirmation)
        {
            session.IsConfirmingClose = true;
            return;
        }

        await CloseSessionAsync(session);
    }

    /// <summary>Confirms a pending close from the inline prompt and tears the session down.</summary>
    [RelayCommand]
    private async Task ConfirmCloseSessionAsync(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
        await CloseSessionAsync(session);
    }

    /// <summary>Dismisses the inline close prompt, keeping the session.</summary>
    [RelayCommand]
    private void CancelCloseSession(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
    }

    [RelayCommand]
    private void ToggleZoom()
    {
        IsZoomed = !IsZoomed;
    }

    /// <summary>
    /// Disposes every live session on app shutdown so each child claude process is killed and releases
    /// its MCP permission-server connection — otherwise those open SSE streams keep the server (and the
    /// whole process) alive after the window closes (bug #32).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions.ToList())
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnSessionCloseRequested;
            await session.DisposeAsync();
        }

        Sessions.Clear();
        _lastStatus.Clear();
    }
}
