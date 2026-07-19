using Avalonia.Threading;
using Microsoft.Extensions.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;

namespace Cockpit.App.ViewModels;

/// <summary>
/// TTY-mode (#9) session panel: hosts a provider's real interactive TUI inside a ConPTY, rendered by
/// <c>TtyView</c>'s terminal control — provider-neutral, so it runs whichever CLI the profile's TTY provider
/// launches (Claude, Codex, …). The profile and its start defaults are chosen up front in the New-session
/// dialog (#31) and handed in via <see cref="LaunchConfigured"/> as the provider's own opaque option values;
/// the view owns the terminal size, so the VM raises <see cref="LaunchRequested"/> and the view launches the
/// carried <see cref="ITtyLauncher"/> with its current columns/rows once it has a size. Read-aloud and status
/// tail the session's transcript through the generic <see cref="ISessionTranscriptReader"/> façade, which
/// dispatches to the profile's provider.
/// </summary>
/// <remarks>
/// Registered <c>ITransientService</c> so <c>CockpitViewModel</c>'s factory mints one per TTY session.
/// The underlying pty host is cross-platform (ConPTY on Windows, Porta.Pty on Linux/macOS), selected
/// by <c>IPtyHostFactory</c>.
/// </remarks>
public partial class TtyViewModel : SessionPanelViewModel, ITransientService
{
    private readonly ITtyLauncher? _launcher;
    private readonly ITtySessionProviderResolver? _providerResolver;
    private readonly ISessionTranscriptReader? _transcriptReader;
    private SessionProfile? _configuredProfile;
    private string? _configuredPermissionMode;
    private string? _configuredModel;
    private string? _configuredEffort;
    private IReadOnlyDictionary<string, string>? _configuredPluginOptions;
    private string? _configuredWorkingDirectory;
    private bool _isLaunchConfigured;
    private SessionResume? _configuredResume;
    private bool _launched;

    /// <summary>
    /// A shell provider handed in directly for a terminal pane (#AC-25), bypassing
    /// <see cref="_providerResolver"/>: a terminal has no profile to resolve through, it just runs a shell. Null for
    /// a normal agent-CLI session, which still resolves its provider from the profile.
    /// </summary>
    private ITtySessionProvider? _configuredProviderOverride;

    /// <summary>
    /// The transcript files that already existed when this session launched, snapshotted once in
    /// <see cref="LaunchConfigured"/> so the read-aloud/status tailers can single out the new <c>.jsonl</c>
    /// <c>claude</c> writes for this session — its id is not forced (undocumented for interactive sessions),
    /// so the transcript is found as the file that appears after launch, not matched by name.
    /// </summary>
    private IReadOnlySet<string>? _transcriptBaseline;

    private CancellationTokenSource? _transcriptTailCancellation;

    // Transcript-driven session status: a TTY panel hosts the real TUI, so there is no event stream to read
    // status from — instead the provider plugin classifies each transcript reading (busy / working-background /
    // done / metadata) and the tracker maps it, so a long thinking pause (which writes no line but
    // is very much busy) stays Busy instead of a quiet-timeout wrongly flipping the dot to Done. Separate from
    // the read-aloud tailer above so status works regardless of the read-aloud toggle. The safety timeout only
    // rescues a busy turn that went silent far past any real turn (a missed end_turn, a killed CLI).
    private static readonly TimeSpan BusySafetyTimeout = TimeSpan.FromSeconds(120);
    private readonly TtyActivityStatusTracker _statusTracker = new(BusySafetyTimeout);

    // Throttles the pty-output liveness keep-alive (AC-75) to ~1 Hz — the terminal flushes at up to 30 fps.
    private DateTimeOffset _lastAliveSignalAt;
    private CancellationTokenSource? _statusTailCancellation;
    private DispatcherTimer? _statusPollTimer;
    private bool _statusTrackingStopped;

    /// <summary>Raised once both the launch is configured and the view is subscribed; the view supplies the terminal size and wires the returned pty.</summary>
    public event Action<TtyLaunchRequest>? LaunchRequested;

    /// <summary>
    /// Raised once a push-to-talk hold finished transcribing (no cleanup applied — TTY is a raw
    /// keystroke stream, so a cleaned-up transcript with different wording would be actively wrong).
    /// The view writes the text as raw bytes to the pty's stdin, the same path as a typed keystroke.
    /// </summary>
    public event Action<string>? VoiceTranscriptReady;

    // Status now lives on the shared SessionPanelViewModel base (AC-37), read by the one SessionHeaderBar.

    /// <summary>One-line render diagnostics (OS, terminal grid, display scale, locale) shown in the TTY header — surfaced so a remote/misrendering machine can be inspected without shell access. Set by the view, which owns the terminal/pty.</summary>
    [ObservableProperty]
    private string _diagnostics = string.Empty;

    /// <summary>AC-34: true while an agent is coupled to this pane through the terminal-access MCP — drives the "agent connected" bar and its Disconnect button. The counterpart to both the human and the agent being able to type: it must always be visible that an agent is on the pane.</summary>
    [ObservableProperty]
    private bool _agentConnected;

    /// <summary>The label on the agent-connected bar ("Agent connected — &lt;session&gt;"), or null when no agent is coupled.</summary>
    [ObservableProperty]
    private string? _agentConnectedLabel;

    /// <summary>The working directory the <c>claude</c> TUI runs in (the configured <c>Claude:WorkingDirectory</c>, else the process cwd — same resolution as <c>ClaudeTtySessionProvider</c>), shown compactly in the header so it is clear which project a session is operating on.</summary>
    [ObservableProperty]
    private string _workingPath = string.Empty;

    /// <summary>
    /// Global TTY terminal font family (#40), mirrored from <c>CockpitViewModel.TerminalFontFamily</c> at
    /// session creation and pushed live on every settings change (see
    /// <c>CockpitViewModel.OnTerminalFontFamilyChanged</c>). Bound in <c>TtyView.axaml</c> straight
    /// onto <c>TerminalControl.FontFamily</c>, which re-measures and reflows the grid on assignment — no
    /// session restart needed.
    /// </summary>
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    /// <summary>Global TTY terminal font size in points (#40); same mirror/live-push wiring as <see cref="TerminalFontFamily"/>.</summary>
    [ObservableProperty]
    private int _terminalFontSize = 13;

    /// <summary>
    /// Mirrors <c>CockpitViewModel.StackSessionsVertically</c> (#24), the multi-session grid's
    /// stacked-vertically layout — seeded at session creation and pushed live on every change (see
    /// <c>CockpitViewModel.OnStackSessionsVerticallyChanged</c>). Bound in <c>TtyView.axaml.cs</c> to
    /// dock the header beside the terminal instead of above it (#54): stacked panels are wide and short,
    /// so a top-docked header burns proportionally more of the little height each panel gets.
    /// </summary>
    [ObservableProperty]
    private bool _isVerticalLayout;

    /// <summary>
    /// This pane runs a plain shell, not an agent CLI (#AC-25). Bound in <c>TtyView.axaml</c> to gate off the
    /// Claude-only header chrome — the limits bars, the working-path-as-Claude line and the plugin header items —
    /// which are meaningless for a shell. The terminal grid itself is provider-neutral and rendered unchanged.
    /// </summary>
    [ObservableProperty]
    private bool _isTerminal;

    // ContextUsedPercent, RateLimits and LimitsTooltip now live on the shared SessionPanelViewModel base (AC-37):
    // the TTY session feeds the base ContextUsedPercent and rebuilds the base RateLimits (5h/wk with reset times)
    // from the statusline relay, so the one SessionHeaderBar control renders its usage pill the same as the SDK one.

    private CancellationTokenSource? _limitsPollCancellation;

    // Parameterless constructor for the Avalonia previewer/Screenshotter design-time context.
    public TtyViewModel()
    {
        ActiveProfileLabel = "work";
        Status = "TTY mode (experiment).";
        KindLabel = "TTY";
        ContextUsedPercent = 42;
        RateLimits.Add(new SessionRateWindow("5h", 64, null));
        RateLimits.Add(new SessionRateWindow("wk", 91, null));
    }

    /// <summary>
    /// Design-time preview of a plain terminal pane (#AC-25/#AC-29) for the Screenshotter: the shared
    /// <see cref="Controls.SessionHeaderBar"/> should render the terminal treatment — kind chip "TTY", no plugin
    /// header host and no usage pill — with the shell name shown once (the title) and only echoed in the cwd
    /// tooltip. Mirrors what <see cref="LaunchTerminal"/> configures, without spawning a real shell.
    /// </summary>
    public static TtyViewModel DesignTerminal()
    {
        var vm = new TtyViewModel
        {
            Title = "Windows PowerShell - 1",
            ActiveProfileLabel = "Windows PowerShell",
            Status = "pwsh 7.4",
            IsTerminal = true,
            ShowPluginHeaderItems = false,
            WorkingDirectory = @"C:\Projects\dotnet\Cockpit",
            SessionStatus = SessionStatus.Busy,
        };
        // A plain shell has no usage feed, so undo the parameterless ctor's SDK-style seeding: without this the
        // ctx pill and 5h/wk windows would show on a terminal header they never have on a real one.
        vm.ContextUsedPercent = null;
        vm.RateLimits.Clear();
        return vm;
    }

    public TtyViewModel(
        ITtyLauncher launcher,
        ITtySessionProviderResolver providerResolver,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ISessionTranscriptReader? transcriptReader = null,
        ITranscriptCleanupService? cleanupService = null,
        IOptions<CockpitOptions>? options = null)
    {
        _launcher = launcher;
        _providerResolver = providerResolver;
        _transcriptReader = transcriptReader;
        KindLabel = "TTY";
        WorkingPath = ResolveWorkingPath(options);
        // Also publish it on the shared base so the read/observe surface reports where this session runs — the
        // TTY working dir is known up front (unlike an SDK session, which learns it from its init event).
        WorkingDirectory = WorkingPath;
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, cleanupService);
    }

    // The effective TTY working directory — the configured Claude:WorkingDirectory when set, else the process
    // cwd. Mirrors ClaudeTtySessionProvider's own resolution so the header shows exactly where the TUI runs.
    private static string ResolveWorkingPath(IOptions<CockpitOptions>? options)
    {
        var configured = options?.Value.Claude.WorkingDirectory;
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    /// <summary>Raw bytes, no cleanup — the terminal has no input box to proofread in, so the transcript goes straight to the pty like a typed keystroke.</summary>
    protected override void OnVoiceTextReady(string text) => VoiceTranscriptReady?.Invoke(text);

    /// <summary>
    /// Auto-submit: writes a carriage return into the pty, the same byte a physical Enter sends after typing —
    /// submits the just-injected transcript to the interactive claude TUI.
    /// <para>
    /// The CR is sent a short beat after the transcript rather than immediately (AC-64). On Windows, ConPTY
    /// coalesces two back-to-back writes — the transcript text, then this CR — into one read, and the TUI folds the
    /// CR into the prompt as a literal newline (a stray □) instead of registering a discrete Enter, so the text is
    /// typed but never sent. A ~60 ms gap (well under the perception threshold) puts the CR in its own pty read so it
    /// lands as a real Enter on every platform. Scheduled on the UI thread, so it is robust whether the request came
    /// from push-to-talk or open-mic.
    /// </para>
    /// </summary>
    protected override void OnVoiceSubmitRequested() => _scheduleAutoSubmit(() => VoiceTranscriptReady?.Invoke("\r"));

    private Action<Action> _scheduleAutoSubmit = _DelayAutoSubmitOnUiThread;

    /// <summary>Test seam (AC-64): run the auto-submit action inline instead of after the ~60 ms UI-thread gap, so the transcript-then-CR ordering is assertable without a real timer.</summary>
    internal void SetAutoSubmitScheduler(Action<Action> scheduler) => _scheduleAutoSubmit = scheduler;

    // The default gap that keeps the CR out of the transcript's ConPTY read (AC-64): a one-shot UI-thread timer.
    private static void _DelayAutoSubmitOnUiThread(Action submit) =>
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(submit, TimeSpan.FromMilliseconds(60)));

    /// <summary>
    /// Configures the panel with the profile and start defaults chosen in the New-session dialog, then
    /// launches the TUI as soon as the view is ready (#31). Replaces the old in-panel Start button and
    /// inline profile picker. <paramref name="permissionMode"/>/<paramref name="model"/>/
    /// <paramref name="effort"/> are launch-only: the real TUI owns any live switching afterwards.
    /// <paramref name="pluginOptions"/> carries the same kind of launch-only start defaults for a plugin
    /// TTY provider's own declared options (Codex's sandbox policy, say) — a Claude session leaves this
    /// null and uses <paramref name="permissionMode"/>/<paramref name="model"/>/<paramref name="effort"/>
    /// instead; the caller never sends both for the same launch.
    /// </summary>
    public void LaunchConfigured(
        SessionProfile? profile,
        string? permissionMode,
        string? model,
        string? effort,
        string? workingDirectory = null,
        SessionResume? resume = null,
        IReadOnlyDictionary<string, string>? pluginOptions = null)
    {
        _configuredProfile = profile;
        _configuredResume = resume;
        _configuredWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        // Show and publish the effective working directory: the per-session override when given, else the
        // global resolution. Keeps the header and the read/observe surface pointing where the TUI actually runs.
        if (_configuredWorkingDirectory is not null)
        {
            WorkingPath = _configuredWorkingDirectory;
            WorkingDirectory = _configuredWorkingDirectory;
        }
        // Read-aloud and status both tail this session's transcript through the generic reader façade, which
        // dispatches to the profile's provider plugin; a profile-less session still records one under the
        // provider's default location, so pass the profile straight through rather than giving up when null.
        // Snapshot the transcripts that exist now, before the TUI spawns and writes its own — the tailers then
        // single out the new record as this session's transcript (its id is not forced).
        _transcriptBaseline = _transcriptReader?.SnapshotTranscripts(profile);
        _configuredPermissionMode = permissionMode;
        _configuredModel = model;
        _configuredEffort = effort;
        _configuredPluginOptions = pluginOptions;
        _isLaunchConfigured = true;
        ActiveProfileLabel = profile?.Label;
        Status = profile is null ? "Launching TUI..." : $"Launching TUI ({profile.Label})...";
        SessionStatus = SessionStatus.Busy;
        TryRaiseLaunch();
    }

    /// <summary>
    /// Configures this panel as a plain terminal running <paramref name="shell"/> (#AC-25), reusing the whole TTY
    /// launch path — the same pty, renderer and view — with a <see cref="ShellTtySessionProvider"/> handed in
    /// directly instead of resolved from a profile. No permission mode, model, MCP or transcript: a shell has none
    /// of that, so the Claude machinery (and the header chrome that shows it) is simply never configured.
    /// </summary>
    public void LaunchTerminal(ShellDescriptor shell, string? workingDirectory = null)
    {
        _configuredProviderOverride = new ShellTtySessionProvider(shell);
        IsTerminal = true;
        // A plain shell is not an agent session, so a plugin session indicator has nothing to say about it (AC-25):
        // hide the shared header's plugin-header host for a terminal pane.
        ShowPluginHeaderItems = false;
        _configuredWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        if (_configuredWorkingDirectory is not null)
        {
            WorkingPath = _configuredWorkingDirectory;
            WorkingDirectory = _configuredWorkingDirectory;
        }

        // Deliberately no transcript baseline: a shell writes no .jsonl, so the read-aloud/status tailers have
        // nothing to follow and must not run for it.
        _isLaunchConfigured = true;
        ActiveProfileLabel = shell.DisplayName;
        Status = $"Launching {shell.DisplayName}...";
        SessionStatus = SessionStatus.Busy;
        TryRaiseLaunch();
    }

    /// <summary>
    /// Raises <see cref="LaunchRequested"/> once both the profile is configured and the view is
    /// subscribed. Called from both sides — the dialog result and the view's subscription — so whichever
    /// happens second fires it; the guard makes it launch exactly once.
    /// </summary>
    public void TryRaiseLaunch()
    {
        // Only commit the launch once there is a subscriber to receive it: if the profile is configured
        // before the view exists, LaunchRequested is still null, so we must not mark it launched yet —
        // the view calls this again once subscribed.
        if (_launched || !_isLaunchConfigured || _launcher is null || LaunchRequested is null)
        {
            return;
        }

        // A terminal hands its shell provider in directly; an agent session resolves one from its profile. With
        // neither the panel is unconfigured (a bare VM in a test/DI probe) — do nothing rather than claim anything.
        if (_configuredProviderOverride is null && _providerResolver is null)
        {
            return;
        }

        // Which TUI this profile runs — the terminal's own shell, Claude's, a plugin's, or none. "None" is a real
        // answer (a local HTTP model is not a program you can put in a terminal) and it is said out loud rather than
        // launched over: the pane reports it instead of quietly starting somebody else's CLI.
        if ((_configuredProviderOverride ?? _providerResolver!.Resolve(_configuredProfile)) is not { } provider)
        {
            _launched = true;
            Status = _configuredProfile is null
                ? "This provider has no terminal interface."
                : $"{_configuredProfile.Label} has no terminal interface — use SDK mode for this provider.";
            SessionStatus = SessionStatus.Idle;

            return;
        }

        _launched = true;
        LaunchRequested.Invoke(new TtyLaunchRequest(
            _launcher,
            provider,
            _configuredProfile,
            _LaunchOptions(),
            _configuredWorkingDirectory,
            _configuredResume));
    }

    /// <summary>
    /// The start defaults in the provider's vocabulary. A blank knob is left out rather than passed as an empty
    /// string: "no model chosen" and "model set to nothing" are different things, and only the first is true here.
    /// Claude's own three knobs and a plugin provider's declared options are never both populated for the same
    /// launch (see <see cref="LaunchConfigured"/>), so layering the plugin options on top here never overwrites
    /// a Claude value with a plugin one.
    /// </summary>
    private Dictionary<string, string> _LaunchOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(TtyLaunchOption.PermissionMode, _configuredPermissionMode);
        Add(TtyLaunchOption.Model, _configuredModel);
        Add(TtyLaunchOption.Effort, _configuredEffort);

        if (_configuredPluginOptions is not null)
        {
            foreach (var (key, value) in _configuredPluginOptions)
            {
                Add(key, value);
            }
        }

        return options;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                options[key] = value;
            }
        }
    }

    /// <summary>
    /// Starts/stops tailing the live session transcript for read-aloud (#35b) as the toggle flips.
    /// Requires the effective config dir (which locates the transcript, resolved at launch even for a
    /// profile-less session) and a wired reader; both are present on the real launch path — only the
    /// design-time/parameterless VM lacks them, where the toggle simply has nothing to tail.
    /// </summary>
    protected override void OnReadAloudToggleChanged(bool isEnabled)
    {
        if (!isEnabled)
        {
            _transcriptTailCancellation?.Cancel();
            _transcriptTailCancellation?.Dispose();
            _transcriptTailCancellation = null;
            return;
        }

        if (_transcriptReader is null || _transcriptBaseline is null || _transcriptTailCancellation is not null)
        {
            return;
        }

        _transcriptTailCancellation = new CancellationTokenSource();
        _ = _TailTranscriptForReadAloudAsync(_configuredProfile, _transcriptBaseline, _transcriptTailCancellation.Token);
    }

    /// <summary>Consumes the transcript tailer and enqueues each assistant turn's prose for TTS — mirrors <c>SessionViewModel._EnqueueTurnProseForReadAloud</c>, just fed by the tailer instead of the SDK event stream.</summary>
    private async Task _TailTranscriptForReadAloudAsync(SessionProfile? profile, IReadOnlySet<string> transcriptBaseline, CancellationToken cancellationToken)
    {
        if (_transcriptReader is null)
        {
            return;
        }

        try
        {
            await foreach (var assistantText in _transcriptReader.ReadAssistantTextAsync(profile, transcriptBaseline, cancellationToken))
            {
                _ = EnqueueReadAloudAsync(assistantText);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the toggle is switched off or the panel closes.
        }
    }

    /// <summary>
    /// Called by the view when the hosted TUI process exits after running (the user closed claude in the
    /// TUI, or it ended). A TTY panel is a live terminal with nothing left to interact with once the
    /// process is gone, so ask the cockpit to close the panel — mirrors closing claude itself.
    /// </summary>
    public void OnProcessExited()
    {
        _StopStatusTracking();
        Status = "TUI process exited.";
        SessionStatus = SessionStatus.Done;
        RaiseCloseRequested();
    }

    /// <summary>
    /// Called by the view once the pty has actually spawned, so the header stops reading "Launching
    /// TUI..." while the real TUI is already interactive below it. Also starts JSONL-driven status
    /// tracking: the session is now idle-waiting-for-you until the transcript shows a turn in flight.
    /// </summary>
    public void OnLaunchSucceeded()
    {
        Status = "Running";
        SessionStatus = SessionStatus.Idle;
        _StartStatusTracking();
    }

    /// <summary>Called by the view when the TUI could not be launched: the panel stays (the error is shown in the terminal) instead of auto-closing.</summary>
    public void OnLaunchFailed()
    {
        _StopStatusTracking();
        Status = "TUI failed to launch.";
        SessionStatus = SessionStatus.Done;
    }

    private void _StartStatusTracking()
    {
        // Needs the transcript reader and the effective config dir (which locates the JSONL, resolved at
        // launch even without a profile) — both are present on the real launch path; the design-time/
        // parameterless VM has neither, so status simply stays Idle there.
        if (_transcriptReader is null || _transcriptBaseline is null || _statusTailCancellation is not null)
        {
            return;
        }

        _statusTailCancellation = new CancellationTokenSource();
        _ = _TailTranscriptForStatusAsync(_configuredProfile, _transcriptBaseline, _statusTailCancellation.Token);

        _statusPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusPollTimer.Tick += _OnStatusPollTick;
        _statusPollTimer.Start();
    }

    /// <summary>Classifies each appended transcript line (busy / done / metadata) and feeds it to the tracker; the tailer runs on a background task, so the status update is marshaled onto the UI thread.</summary>
    private async Task _TailTranscriptForStatusAsync(SessionProfile? profile, IReadOnlySet<string> transcriptBaseline, CancellationToken cancellationToken)
    {
        if (_transcriptReader is null)
        {
            return;
        }

        try
        {
            await foreach (var reading in _transcriptReader.ReadActivityAsync(profile, transcriptBaseline, cancellationToken))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_statusTrackingStopped)
                    {
                        SessionStatus = _statusTracker.OnActivity(reading.Activity, DateTimeOffset.UtcNow);
                    }

                    // Surface the raw transcript line to the read/observe surface: it carries any output signal
                    // (a pull-request url printed by gh, a merged/closed line) as a substring regardless of which
                    // JSONL field holds it, which is exactly what a substring-scanning watcher needs. A synthetic
                    // keep-alive reading (background sub-agent activity) has no line, so there is nothing to scan.
                    if (reading.RawLine is { } line)
                    {
                        RaiseOutputText(line);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the panel closes or the process exits.
        }
        catch (Exception)
        {
            // A transient IO fault while tailing (the JSONL file momentarily locked, a read error) must
            // not leave the poll timer quietly decaying the dot to a false Done while the TUI is still
            // busy — stop tracking so the status freezes at its last real value instead. Runs on the
            // tailer's thread, so the timer/token teardown is marshaled onto the UI thread.
            Dispatcher.UIThread.Post(_StopStatusTracking);
        }
    }

    private void _OnStatusPollTick(object? sender, EventArgs e)
    {
        if (_statusTrackingStopped)
        {
            return;
        }

        SessionStatus = _statusTracker.Poll(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The pty produced output — the TUI is still drawing (a thinking spinner ticking, text streaming), so the
    /// session is visibly alive (AC-75). Keeps the status tracker's safety-timeout clock fresh while a turn is busy,
    /// so a long but visibly-working think/plan phase never decays to a false Done. Throttled to ~1 Hz — the timeout
    /// is generous and the terminal can flush at up to 30 fps — and a truly stalled/killed CLI produces no output,
    /// so its turn still times out to Done. Called on the UI thread from the view's output flush.
    /// </summary>
    public void NotifyTerminalOutput()
    {
        if (_statusTrackingStopped)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAliveSignalAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastAliveSignalAt = now;
        SessionStatus = _statusTracker.OnAlive(now);
    }

    private void _StopStatusTracking()
    {
        _statusTrackingStopped = true;
        _statusTailCancellation?.Cancel();
        _statusTailCancellation?.Dispose();
        _statusTailCancellation = null;

        if (_statusPollTimer is not null)
        {
            _statusPollTimer.Stop();
            _statusPollTimer.Tick -= _OnStatusPollTick;
            _statusPollTimer = null;
        }
    }

    /// <summary>
    /// Starts reading this session's limits from the file the provider plugin's statusline writes.
    /// Polled rather than watched: the file is rewritten whole every few seconds by a shell script, and a
    /// filesystem watcher on a write-then-rename fires more often than it tells you anything.
    /// </summary>
    public void TrackLimits(string? statusFile)
    {
        if (string.IsNullOrWhiteSpace(statusFile))
        {
            return;
        }

        _limitsPollCancellation = new CancellationTokenSource();
        var cancellation = _limitsPollCancellation.Token;

        _ = Task.Run(
            async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(statusFile)
                            && SessionLimits.TryParse(await File.ReadAllTextAsync(statusFile, cancellation)) is { HasAny: true } limits)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ContextUsedPercent = limits.ContextUsedPercent;
                                RateLimits.Clear();
                                if (limits.FiveHourUsedPercent is { } fiveHour)
                                {
                                    RateLimits.Add(new SessionRateWindow("5h", fiveHour, limits.FiveHourResetsAt));
                                }

                                if (limits.SevenDayUsedPercent is { } sevenDay)
                                {
                                    RateLimits.Add(new SessionRateWindow("wk", sevenDay, limits.SevenDayResetsAt));
                                }

                                LimitsTooltip = DescribeLimits(limits);
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // A file caught mid-rename, a session that just ended. The next tick sorts it out; a status
                        // bar must never be a reason for a session to fall over.
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false);
                }
            },
            cancellation);
    }

    /// <summary>
    /// The hover text: what the bars mean, spelled out, plus when each window rolls over — which is the one thing
    /// a bar cannot say and the thing you actually want when it is nearly full. Only the numbers Claude reported,
    /// so nothing here is invented.
    /// </summary>
    internal static string DescribeLimits(SessionLimits limits) => limits.Describe();

    protected override ValueTask DisposeCoreAsync()
    {
        // The terminal control owns the pty lifetime (it created it via the launcher); it disposes
        // the ConPtyProcess on unload/close. The transcript tailer is this VM's own background loop,
        // so it does need stopping here — otherwise it would keep polling a file for a session that no
        // longer has a panel to read aloud into.
        _transcriptTailCancellation?.Cancel();
        _transcriptTailCancellation?.Dispose();
        _transcriptTailCancellation = null;
        _limitsPollCancellation?.Cancel();
        _limitsPollCancellation?.Dispose();
        _limitsPollCancellation = null;
        _StopStatusTracking();
        return ValueTask.CompletedTask;
    }
}
