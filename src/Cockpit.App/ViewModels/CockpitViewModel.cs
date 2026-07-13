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
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Audio;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Shortcuts;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Multi-instance cockpit shell: owns the collection of running <see cref="SessionViewModel"/>
/// panels, which one is selected, and the grid/zoom view mode. Reuses the existing
/// <see cref="SessionViewModel"/>/<c>SessionView</c> per panel — this view model only
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

    private readonly Func<SessionViewModel>? _sessionFactory;
    private readonly Func<ClaudeTtyViewModel>? _ttySessionFactory;
    private readonly ISessionDialogService? _dialogService;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly IShortcutSettingsStore? _shortcutSettingsStore;
    private ShortcutSettings _shortcutSettings = ShortcutSettings.Default;
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

    // "Everything is quiet" is edge-triggered too: announced when the last working session falls idle, and armed
    // again only once something starts working, so a cockpit left alone does not repeat itself every sweep.
    private bool _allSessionsIdleNotified = true;

    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

    /// <summary>Left-menu accordion sections contributed by plugins (#14), shown under the session list. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSideSection> PluginSideSections { get; } = [];

    /// <summary>Left-menu launcher buttons contributed by plugins (#14); clicking one runs the plugin's action (typically opening a dialog).</summary>
    public ObservableCollection<PluginSideButton> PluginSideButtons { get; } = [];

    /// <summary>Keyboard shortcuts contributed by plugins (#: shortcuts), dispatched alongside the built-in app-action shortcuts.</summary>
    public ObservableCollection<PluginShortcut> PluginShortcuts { get; } = [];

    /// <summary>The currently-active shortcuts (app actions + plugin shortcuts) the view matches key presses against. Rebuilt when settings or plugin shortcuts change.</summary>
    public IReadOnlyList<ShortcutBinding> ActiveShortcuts { get; private set; } = [];

    /// <summary>Rows for the Options → Shortcuts tab: the editable app-action gestures, then the read-only plugin-contributed ones.</summary>
    public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; } = [];

    /// <summary>Per-plugin settings views (#14) keyed by plugin folder id, opened from the gear in the plugin manager.</summary>
    public Dictionary<string, Func<Control>> PluginSettings { get; } = [];

    /// <summary>Settings-saved callbacks (#52) keyed by plugin folder id, registered via <see cref="ICockpitHost.OnSettingsSaved"/> and run once that plugin's settings dialog Save() returns true.</summary>
    private readonly Dictionary<string, List<Action>> _settingsSavedHandlers = [];

    /// <summary>The "Plugins" Options tab (#14): install/enable/disable/remove installed plugins. Loaded when the Options dialog opens.</summary>
    public PluginManagerViewModel Plugins { get; }

    /// <summary>The delegated-tasks view (#67): work other sessions handed to a profile, which has no tab of its own.</summary>
    public DelegatedTasksViewModel DelegatedTasks { get; }

    /// <summary>Owns the live toast collection (#61); <see cref="Toasts"/> below is what <c>CockpitView.axaml</c>'s overlay actually binds to.</summary>
    public ToastHostViewModel ToastHost { get; } = new();

    /// <summary>Toasts currently shown by the overlay (#61), fed by <see cref="Services.ToastService"/> via <see cref="ToastHost"/>.</summary>
    public ObservableCollection<ToastViewModel> Toasts => ToastHost.Toasts;

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
            1 => $"A plugin failed to load: {failures[0].DisplayName}. See the Plugin store → Installed for details.",
            _ => $"{failures.Count} plugins failed to load. See the Plugin store → Installed for details.",
        };
    }

    [RelayCommand]
    private void DismissPluginFailures() => HasPluginFailures = false;

    void IPluginContributionSink.AddPluginSideSection(string title, Func<Control> createView) =>
        _OnUiThread(() => PluginSideSections.Add(new PluginSideSection(title, createView)));

    void IPluginContributionSink.AddPluginSideButton(string title, Action onInvoke) =>
        _OnUiThread(() => PluginSideButtons.Add(new PluginSideButton(title, onInvoke)));

    void IPluginContributionSink.AddPluginShortcut(PluginShortcut shortcut) =>
        _OnUiThread(() => PluginShortcuts.Add(shortcut));

    void IPluginContributionSink.AddPluginSettings(string pluginId, Func<Control> createView) =>
        _OnUiThread(() => PluginSettings[pluginId] = createView);

    // Unlike the three contributions above, registration here touches only this private dictionary — never
    // a bound ObservableCollection — and both members are reached exclusively from Avalonia UI-thread
    // callbacks in practice (a contribution's own constructor, and the settings dialog's Save click), so no
    // dispatcher hop is needed. Kept synchronous rather than routed through _OnUiThread — that hop only
    // actually runs when something later pumps the dispatcher queue, which a unit test never does.
    void IPluginContributionSink.AddSettingsSavedHandler(string pluginId, Action callback)
    {
        if (!_settingsSavedHandlers.TryGetValue(pluginId, out var handlers))
        {
            handlers = [];
            _settingsSavedHandlers[pluginId] = handlers;
        }

        handlers.Add(callback);
    }

    void IPluginContributionSink.NotifySettingsSaved(string pluginId)
    {
        if (!_settingsSavedHandlers.TryGetValue(pluginId, out var handlers))
        {
            return;
        }

        // Snapshot before invoking: a handler could itself register another (unlikely, but avoids mutating
        // the list while iterating it).
        foreach (var handler in handlers.ToArray())
        {
            handler();
        }
    }

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

    /// <summary>
    /// True whenever the multi-pane grid is showing (two or more sessions, not the single-pane/zoom layout):
    /// every pane then carries the drag-reorder grip, and the column/row gutters between them are resizable.
    /// Covers the vertical column, the side-by-side row, and the 2×2 alike — they're one draggable grid.
    /// </summary>
    public bool StackSessionsInStack => !ShowSinglePane && Sessions.Count >= 2;

    /// <summary>When true, closing the window hides it to the system tray and keeps the app running (#33). Read by MainWindow on close.</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    /// <summary>
    /// Width in pixels of the left sidebar column (#49), dragged via the <c>GridSplitter</c> in
    /// <c>CockpitView.axaml</c> and persisted so it survives a restart. The splitter's column already
    /// enforces <see cref="LayoutSettings.MinSidebarWidth"/>/<see cref="LayoutSettings.MaxSidebarWidth"/>
    /// while dragging; <see cref="LoadLayoutSettingsAsync"/> and <c>LayoutSettingsStore</c> clamp again
    /// defensively for a value read from a hand-edited <c>cockpit.json</c>.
    /// </summary>
    [ObservableProperty]
    private double _sidebarWidth = LayoutSettings.DefaultSidebarWidth;

    [ObservableProperty]
    private string _layoutSettingsStatus = string.Empty;

    /// <summary>
    /// Global TTY terminal font family (#40) — one setting for every TTY session, not per-profile or
    /// per-session. The effective value fed straight into <c>TerminalControl.FontFamily</c>, so both a
    /// single family name and a comma-separated fallback list work. Driven by the Options dropdown
    /// (<see cref="TerminalFontSelection"/>): a curated choice sets it directly, the "Custom…" choice
    /// mirrors <see cref="TerminalCustomFontFamily"/>.
    /// </summary>
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    /// <summary>Global TTY terminal font size in points (#40), clamped to <see cref="Cockpit.Core.Terminal.TerminalSettings.MinFontSize"/>-<see cref="Cockpit.Core.Terminal.TerminalSettings.MaxFontSize"/> on save.</summary>
    [ObservableProperty]
    private int _terminalFontSize = 13;

    /// <summary>Selected item in the Options font-family dropdown (#40) — a curated family or <see cref="CustomFontChoice"/>. Drives <see cref="TerminalFontFamily"/> and toggles <see cref="IsTerminalFontCustom"/>.</summary>
    [ObservableProperty]
    private string _terminalFontSelection = "Cascadia Mono, Consolas, monospace";

    /// <summary>True when the font-family dropdown is on "Custom…" (#40), revealing the free-text box bound to <see cref="TerminalCustomFontFamily"/>.</summary>
    [ObservableProperty]
    private bool _isTerminalFontCustom;

    /// <summary>Free-text font family entered when the dropdown is on "Custom…" (#40); mirrored into <see cref="TerminalFontFamily"/> while custom is active.</summary>
    [ObservableProperty]
    private string _terminalCustomFontFamily = string.Empty;

    [ObservableProperty]
    private string _terminalSettingsStatus = string.Empty;

    /// <summary>Sentinel item in the font-family dropdown (#40) that switches to a free-text box for any font not in the curated list.</summary>
    public const string CustomFontChoice = "Custom…";

    /// <summary>Curated monospace font choices offered by the Options dialog's Terminal font-family dropdown; any font not listed is reachable via <see cref="CustomFontChoice"/>.</summary>
    public IReadOnlyList<string> TerminalFontFamilies { get; } =
    [
        "Cascadia Mono, Consolas, monospace",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "DejaVu Sans Mono",
        "Courier New",
    ];

    /// <summary>Items for the Options font-family dropdown (#40): the curated families plus the "Custom…" sentinel.</summary>
    public IReadOnlyList<string> TerminalFontChoices => [.. TerminalFontFamilies, CustomFontChoice];

    /// <summary>Maps the dropdown selection to the effective font family (#40): "Custom…" reveals the free-text box and uses its value, any other choice is used directly.</summary>
    partial void OnTerminalFontSelectionChanged(string value)
    {
        if (value == CustomFontChoice)
        {
            IsTerminalFontCustom = true;
            if (!string.IsNullOrWhiteSpace(TerminalCustomFontFamily))
            {
                TerminalFontFamily = TerminalCustomFontFamily;
            }
        }
        else
        {
            IsTerminalFontCustom = false;
            TerminalFontFamily = value;
        }
    }

    /// <summary>While the dropdown is on "Custom…" (#40), keeps the effective font family in sync with the free-text box.</summary>
    partial void OnTerminalCustomFontFamilyChanged(string value)
    {
        if (IsTerminalFontCustom && !string.IsNullOrWhiteSpace(value))
        {
            TerminalFontFamily = value;
        }
    }

    /// <summary>Aligns the dropdown/custom-box state with the effective <see cref="TerminalFontFamily"/> (#40) — used after loading from the store so a saved custom font reopens in the "Custom…" state.</summary>
    private void SyncTerminalFontSelectionFromFamily()
    {
        if (TerminalFontFamilies.Contains(TerminalFontFamily))
        {
            IsTerminalFontCustom = false;
            TerminalCustomFontFamily = string.Empty;
            TerminalFontSelection = TerminalFontFamily;
        }
        else
        {
            TerminalCustomFontFamily = TerminalFontFamily;
            IsTerminalFontCustom = true;
            TerminalFontSelection = CustomFontChoice;
        }
    }

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

    /// <summary>Pushes the vertical-stack layout signal to every open TTY session as it changes (#54), so a switch to/from stacked-vertically re-docks each panel's header live, the same as flipping the terminal font settings.</summary>
    partial void OnStackSessionsVerticallyChanged(bool value)
    {
        OnPropertyChanged(nameof(StackSessionsInStack));
        foreach (var session in Sessions)
        {
            if (session is ClaudeTtyViewModel tty)
            {
                tty.IsVerticalLayout = value;
            }
        }
    }

    /// <summary>True when only the selected session should be shown full-size — either the persisted single layout (#24) or a transient Zoom.</summary>
    public bool ShowSinglePane => SingleSessionLayout || IsZoomed;

    partial void OnIsZoomedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(StackSessionsInStack));
        RefreshPaneVisibility();
    }

    partial void OnSingleSessionLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(ShowZoomButton));
        OnPropertyChanged(nameof(StackSessionsInStack));
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

    /// <summary>Minutes a finished session stays "done" before it falls back to idle. 0 leaves it on "done" forever. Distinct from <see cref="IdleThresholdMinutes"/>, which is about the operator being away.</summary>
    [ObservableProperty]
    private int _sessionIdleMinutes = (int)SessionIdleDecision.DefaultIdleThreshold.TotalMinutes;

    /// <summary>Whether a session that finished its turn announces itself when the operator is not watching it.</summary>
    [ObservableProperty]
    private bool _notifyOnSessionFinished = true;

    /// <summary>Whether a session announces that it has gone idle.</summary>
    [ObservableProperty]
    private bool _notifyOnSessionIdle;

    /// <summary>Whether one message is sent when the last session goes idle — nothing is running any more.</summary>
    [ObservableProperty]
    private bool _notifyWhenAllSessionsIdle;

    /// <summary>
    /// Whether the cockpit window is the focused one. Set by the window itself (it is the only thing that knows),
    /// and read by the finished-session notification: a session you are looking at does not need to announce itself.
    /// </summary>
    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private string _notificationSettingsStatus = string.Empty;

    /// <summary>One shared "Saved" indicator for the Options dialog's single footer Save (#13), shown next to the Save button instead of a per-section label.</summary>
    [ObservableProperty]
    private string _allSettingsStatus = string.Empty;

    [ObservableProperty]
    private string _shortcutSettingsStatus = string.Empty;

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

    /// <summary>Keeps each session's <see cref="SessionViewModel.IsSelected"/> in sync with the active selection.</summary>
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
    // seeds three sample sessions across different providers and statuses so the render shows the
    // overview + grid without a real DI-backed session behind each one.
    public CockpitViewModel()
    {
        var waiting = new SessionViewModel { Title = "Session 1", ActiveProfileLabel = "work (Claude)", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new SessionViewModel { Title = "Session 2", ActiveProfileLabel = "local (Ollama)", SessionStatus = SessionStatus.Busy };
        var tty = new ClaudeTtyViewModel { Title = "Session 3", ActiveProfileLabel = "personal (Claude TTY)", SessionStatus = SessionStatus.Busy };

        Sessions.Add(waiting);
        Sessions.Add(busy);
        Sessions.Add(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
        Plugins = new PluginManagerViewModel();
        DelegatedTasks = new DelegatedTasksViewModel();

        // Seed the Options → Shortcuts rows from the catalog defaults; without a settings store the DI path
        // that normally builds them never runs, and the tab would render empty in the previewer/screenshotter.
        _RebuildShortcutRows();
    }

    public CockpitViewModel(
        Func<SessionViewModel> sessionFactory,
        Func<ClaudeTtyViewModel> ttySessionFactory,
        ISessionDialogService dialogService,
        IAudioCaptureService captureService,
        IAudioPlaybackService playbackService,
        IAttentionNotifier attentionNotifier,
        INotificationSettingsStore notificationSettingsStore,
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
        IAudioDeviceProvider? audioDeviceProvider = null,
        IAppRestartService? appRestartService = null,
        IShortcutSettingsStore? shortcutSettingsStore = null,
        DelegatedTasksViewModel? delegatedTasks = null)
    {
        DelegatedTasks = delegatedTasks ?? new DelegatedTasksViewModel();
        _audioDeviceProvider = audioDeviceProvider;
        _pluginDiagnostics = pluginDiagnostics;
        _shortcutSettingsStore = shortcutSettingsStore;
        // The full plugin manager needs its store/installer/bootstrap, store dependencies, the dialog host
        // and the diagnostics; when they are absent (unit tests that don't exercise plugins) the design-time
        // manager is used, so the tab is inert.
        Plugins = pluginRegistrationStore is not null && pluginInstaller is not null && pluginBootstrap is not null
                && pluginStoreConfigStore is not null && pluginStoreClient is not null && pluginDialogHost is not null
                && pluginDiagnostics is not null
            ? new PluginManagerViewModel(pluginRegistrationStore, pluginInstaller, pluginBootstrap, dialogService, pluginStoreConfigStore, pluginStoreClient, PluginSettings, pluginDialogHost, pluginDiagnostics, this, appRestartService)
            : new PluginManagerViewModel();
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _dialogService = dialogService;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
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
            OnPropertyChanged(nameof(StackSessionsInStack));
            RefreshPaneVisibility();
        };
        _ = LoadNotificationSettingsAsync();
        _ = LoadTranscriptDisplaySettingsAsync();
        _ = LoadSessionBehaviorSettingsAsync();
        _ = LoadLayoutSettingsAsync();
        _ = LoadVoiceSettingsAsync();
        _ = LoadTerminalSettingsAsync();
        _ = LoadShortcutSettingsAsync();

        // Plugin shortcuts arrive as plugins initialize; each changes the active bindings and the Options list.
        PluginShortcuts.CollectionChanged += (_, _) =>
        {
            _RebuildActiveShortcuts();
            _RebuildShortcutRows();
        };
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
        SessionIdleMinutes = (int)settings.SessionIdleThreshold.TotalMinutes;
        NotifyOnSessionFinished = settings.NotifyOnSessionFinished;
        NotifyOnSessionIdle = settings.NotifyOnSessionIdle;
        NotifyWhenAllSessionsIdle = settings.NotifyWhenAllSessionsIdle;
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
            NotifyOnSessionFinished = NotifyOnSessionFinished,
            NotifyOnSessionIdle = NotifyOnSessionIdle,
            NotifyWhenAllSessionsIdle = NotifyWhenAllSessionsIdle,
            // 0 is a real choice here ("never let a session go idle"), so it is saved as written rather than
            // being nudged back to the default the way the away-threshold is.
            SessionIdleThreshold = SessionIdleMinutes > 0 ? TimeSpan.FromMinutes(SessionIdleMinutes) : TimeSpan.Zero,
        };

        await _notificationSettingsStore.SaveAsync(settings);
        NotificationSettingsStatus = "✓ Saved";
    }

    private async Task LoadShortcutSettingsAsync()
    {
        if (_shortcutSettingsStore is not null)
        {
            _shortcutSettings = await _shortcutSettingsStore.LoadAsync();
        }

        _RebuildShortcutRows();
        _RebuildActiveShortcuts();
    }

    /// <summary>Persists the keyboard shortcuts edited in the Options → Shortcuts tab to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveShortcutSettingsAsync()
    {
        // Fold the editable rows back into the settings, then re-arm the live bindings so a change takes effect
        // immediately without a restart.
        var settings = _shortcutSettings;
        foreach (var row in ShortcutRows)
        {
            settings = row.Action is { } action
                ? settings.With(action, row.Gesture)
                : row.PluginShortcutId is { } id
                    ? settings.WithPlugin(id, row.Gesture)
                    : settings;
        }

        _shortcutSettings = settings;
        _RebuildActiveShortcuts();

        if (_shortcutSettingsStore is not null)
        {
            await _shortcutSettingsStore.SaveAsync(settings);
        }

        ShortcutSettingsStatus = "✓ Saved";
    }

    // The Options list: one editable row per app action (label + configured gesture), then a read-only row per
    // plugin-contributed shortcut so the operator can see what plugins bound.
    private void _RebuildShortcutRows()
    {
        ShortcutRows.Clear();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            ShortcutRows.Add(new ShortcutRowViewModel(descriptor.Label, descriptor.Action, _shortcutSettings.GestureFor(descriptor.Action)));
        }

        foreach (var shortcut in PluginShortcuts)
        {
            ShortcutRows.Add(new ShortcutRowViewModel(
                $"{shortcut.Title} (plugin)",
                shortcut.Id,
                _shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture)));
        }
    }

    // The live dispatch table the view matches against: every bound app action (blank = unbound, skipped) plus
    // every plugin shortcut, each paired with the action to run.
    private void _RebuildActiveShortcuts()
    {
        var bindings = new List<ShortcutBinding>();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            var gesture = _shortcutSettings.GestureFor(descriptor.Action);
            if (!string.IsNullOrWhiteSpace(gesture))
            {
                // The command palette is the one shortcut that must open even while typing in a session/terminal.
                var alwaysActive = descriptor.Action == ShortcutAction.CommandPalette;
                bindings.Add(new ShortcutBinding(
                    gesture,
                    descriptor.Label,
                    () => _InvokeAppAction(descriptor.Action),
                    alwaysActive,
                    ShortcutCatalog.StaysActiveInTerminal(descriptor.Action)));
            }
        }

        foreach (var shortcut in PluginShortcuts)
        {
            var gesture = _shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture);
            if (!string.IsNullOrWhiteSpace(gesture))
            {
                bindings.Add(new ShortcutBinding(gesture, shortcut.Title, shortcut.OnInvoke));
            }
        }

        ActiveShortcuts = bindings;
    }

    // Runs the command behind an app-action shortcut. Commands are the same ones the main menu binds to.
    private void _InvokeAppAction(ShortcutAction action)
    {
        // Duplicate takes the active session as its parameter, unlike the parameterless app commands below.
        if (action == ShortcutAction.DuplicateSession)
        {
            if (SelectedSession is { } session && DuplicateSessionCommand.CanExecute(session))
            {
                DuplicateSessionCommand.Execute(session);
            }

            return;
        }

        System.Windows.Input.ICommand? command = action switch
        {
            ShortcutAction.NewSession => NewSessionCommand,
            ShortcutAction.ManageProfiles => ManageProfilesCommand,
            ShortcutAction.McpServers => OpenMcpServersCommand,
            ShortcutAction.PluginStore => Plugins.OpenStoreDialogCommand,
            ShortcutAction.Options => OptionsCommand,
            ShortcutAction.About => AboutCommand,
            ShortcutAction.ToggleZoom => ToggleZoomCommand,
            ShortcutAction.CommandPalette => ShowCommandPaletteCommand,
            ShortcutAction.PreviousSession => SelectPreviousSessionCommand,
            ShortcutAction.NextSession => SelectNextSessionCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
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
        SidebarWidth = settings.SidebarWidth;
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
            SidebarWidth = SidebarWidth,
        });
        LayoutSettingsStatus = "✓ Saved";
    }

    /// <summary>
    /// Persists the sidebar width alone (#49), called from the view when the <c>GridSplitter</c> drag
    /// ends — a direct-manipulation UI setting that should save immediately, unlike the Options-dialog
    /// settings above which wait for the dialog's own Save. Clamped before both the property assignment
    /// and the save so an out-of-range drag (shouldn't happen given the column's own min/max) can't
    /// persist.
    /// </summary>
    public async Task SetSidebarWidthAsync(double width)
    {
        SidebarWidth = Math.Clamp(width, LayoutSettings.MinSidebarWidth, LayoutSettings.MaxSidebarWidth);

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(new LayoutSettings
        {
            SingleSessionLayout = SingleSessionLayout,
            StackSessionsVertically = StackSessionsVertically,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            SidebarWidth = SidebarWidth,
        });
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
        SyncTerminalFontSelectionFromFamily();
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
            await session.StartConfiguredAsync(result.Profile, result.Mode, result.Model, result.Effort, result.EnabledMcpServerNames, result.WorkingDirectory, result.Resume);
        }
        else
        {
            var session = _ttySessionFactory();
            session.LaunchResult = result;
            AddSession(session, result.SessionName, result.Profile.Label);
            session.LaunchConfigured(result.Profile, result.Mode.Value, result.Model.Value, result.Effort.Value, result.WorkingDirectory, result.Resume);
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
    /// Opens the plugin store dialog (#62) with the "Available updates" filter preselected (#65) — the
    /// action button on a plugin-update toast, so the operator lands straight on the updates list instead
    /// of the full Options→Plugins tab. Skips the audio-device refresh <see cref="OptionsAsync"/> does
    /// since it is irrelevant here.
    /// </summary>
    public async Task OpenPluginStoreUpdatesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await Plugins.LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(Plugins, PluginStoreFilter.UpdatesAvailable);
    }

    /// <summary>Opens the About dialog (#46) from the sidebar: app name, version, description and links.</summary>
    [RelayCommand]
    private async Task AboutAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAboutDialogAsync();
    }

    /// <summary>
    /// Opens the delegated-tasks view (#67): the work other sessions handed to a profile. Those tasks run as
    /// sessions with no tab of their own, so this is where they stay visible — and stoppable.
    /// </summary>
    [RelayCommand]
    private async Task ShowDelegatedTasksAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowDelegatedTasksDialogAsync();
    }

    /// <summary>Opens the command palette (#: command palette): a searchable list of every app action and plugin command with its shortcut.</summary>
    [RelayCommand]
    private async Task ShowCommandPaletteAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowCommandPaletteDialogAsync(_BuildPaletteCommands());
    }

    // Every command the palette can run: the built-in app actions (except the palette itself) and every
    // plugin-contributed command, each with its shortcut shown. Plugins appear here just by registering a
    // shortcut — one with no gesture is a palette-only command.
    private IReadOnlyList<PaletteCommand> _BuildPaletteCommands()
    {
        var commands = new List<PaletteCommand>();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            if (descriptor.Action == ShortcutAction.CommandPalette)
            {
                continue;
            }

            commands.Add(new PaletteCommand(
                descriptor.Label,
                _PrettifyGesture(_shortcutSettings.GestureFor(descriptor.Action)),
                () => _InvokeAppAction(descriptor.Action)));
        }

        foreach (var shortcut in PluginShortcuts)
        {
            commands.Add(new PaletteCommand(
                shortcut.Title,
                _PrettifyGesture(_shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture)),
                shortcut.OnInvoke));
        }

        return commands;
    }

    // "Ctrl+Shift+P" -> "Ctrl + Shift + P" for the palette's shortcut column; blank stays blank.
    private static string _PrettifyGesture(string gesture) =>
        string.IsNullOrWhiteSpace(gesture) ? string.Empty : gesture.Replace("+", " + ");

    /// <summary>
    /// Persists every options section in one go — the Options dialog's single footer Save (#13)
    /// replaces the six per-section Save buttons the flyout used to have.
    /// </summary>
    [RelayCommand]
    private async Task SaveAllSettingsAsync()
    {
        await SaveNotificationSettingsCommand.ExecuteAsync(null);
        await SaveTranscriptDisplaySettingsCommand.ExecuteAsync(null);
        await SaveSessionBehaviorSettingsCommand.ExecuteAsync(null);
        await SaveLayoutSettingsCommand.ExecuteAsync(null);
        await SaveVoiceSettingsCommand.ExecuteAsync(null);
        await SaveTerminalSettingsCommand.ExecuteAsync(null);
        await SaveShortcutSettingsCommand.ExecuteAsync(null);
        AllSettingsStatus = "✓ Saved";
    }

    private void AddSession(SessionPanelViewModel session, string? name, string profileLabel)
    {
        _sessionCounter++;
        // A friendly name from the dialog wins; otherwise fall back to "<profile> - <N>" so the sidebar
        // shows which profile — and therefore which provider — each session runs under.
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
            // Seed the current stacked-vertically layout (#54); OnStackSessionsVerticallyChanged keeps it
            // live afterwards, same pattern as the font settings above.
            tty.IsVerticalLayout = StackSessionsVertically;
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

        // A turn just finished. Worth saying out loud only when you are not looking at that session — the
        // notifier makes that call, since it is the one that knows whether you are even at the PC.
        if (session.SessionStatus == SessionStatus.Done && previous == SessionStatus.Busy)
        {
            NotifySessionFinished(session);
        }

        // Anything other than idle means there is work in the cockpit again, so the next fall to complete
        // silence is news once more.
        if (session.SessionStatus != SessionStatus.Idle)
        {
            _allSessionsIdleNotified = false;
        }
    }

    /// <summary>
    /// Lets finished sessions fall back to idle once they have been quiet for the configured time, and announces
    /// that — per session, and once more when the last of them goes quiet so the cockpit as a whole is idle.
    /// Driven by a periodic sweep rather than a timer per session: one tick decides for all of them.
    /// </summary>
    /// <param name="now">The current time, injected so the sweep is testable without waiting for it.</param>
    internal void SweepIdleSessions(DateTimeOffset now)
    {
        var threshold = SessionIdleMinutes > 0 ? TimeSpan.FromMinutes(SessionIdleMinutes) : TimeSpan.Zero;

        foreach (var session in Sessions)
        {
            if (!SessionIdleDecision.BecomesIdle(session.SessionStatus == SessionStatus.Done, session.LastActivityUtc, now, threshold))
            {
                continue;
            }

            session.SessionStatus = SessionStatus.Idle;
            NotifySessionIdle(session, threshold);
        }

        if (!_allSessionsIdleNotified && Sessions.Count > 0 && Sessions.All(session => session.SessionStatus == SessionStatus.Idle))
        {
            _allSessionsIdleNotified = true;
            _ = _attentionNotifier?.NotifyAllSessionsIdleAsync();
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

    private void NotifySessionFinished(SessionPanelViewModel session)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var notification = new AttentionNotification(session.Title, "Done");
        _ = _attentionNotifier.NotifySessionFinishedAsync(notification, ReferenceEquals(session, SelectedSession), IsWindowActive);
    }

    private void NotifySessionIdle(SessionPanelViewModel session, TimeSpan threshold)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var minutes = (int)threshold.TotalMinutes;
        var notification = new AttentionNotification(session.Title, $"Idle for {minutes} minute(s)");
        _ = _attentionNotifier.NotifySessionIdleAsync(notification);
    }

    [RelayCommand]
    private void SelectSession(SessionPanelViewModel session)
    {
        SelectedSession = session;
    }

    /// <summary>
    /// Moves the selection to the previous session in <see cref="Sessions"/>, wrapping from the first
    /// to the last. No-op when there are no sessions; selects the only session when there is exactly
    /// one. Bound to the configurable <see cref="ShortcutAction.PreviousSession"/> shortcut (Ctrl+Up by default).
    /// </summary>
    [RelayCommand]
    public void SelectPreviousSession() => _StepSelection(-1);

    /// <summary>
    /// Moves the selection to the next session in <see cref="Sessions"/>, wrapping from the last to
    /// the first. No-op when there are no sessions. Bound to the configurable
    /// <see cref="ShortcutAction.NextSession"/> shortcut (Ctrl+Down by default).
    /// </summary>
    [RelayCommand]
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
