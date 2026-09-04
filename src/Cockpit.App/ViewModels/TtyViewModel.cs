using System.Text;
using Avalonia.Threading;
using Microsoft.Extensions.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using Cockpit.Core.Usage;

namespace Cockpit.App.ViewModels;

// AC-1013: TTY-mode (#9) session panel: hosts a provider's real interactive TUI inside a ConPTY,...
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
    private SessionResources? _configuredContributed;
    private bool _launched;

    // AC-1013: True from `LaunchConfigured` until `OnLaunchSucceeded` for the first launch of a
    private bool _degradeInsteadOfCloseOnExit;

    // The offer this pane was restored with, captured when `_degradeInsteadOfCloseOnExit` armed — the source a failed exit degrades back to, since `SessionPanelViewModel.RestoreOffer` is already null by then.
    private SessionRestorePlan? _restoredOfferSnapshot;

    // A shell provider handed in directly for a terminal pane (#AC-25), bypassing
    // `_providerResolver`: a terminal has no profile to resolve through, it just runs a shell. Null for
    // a normal agent-CLI session, which still resolves its provider from the profile.
    private ITtySessionProvider? _configuredProviderOverride;

    // AC-1013: The transcript files that already existed when this session launched, snapshotted once in
    private IReadOnlySet<string>? _transcriptBaseline;

    // AC-1013: Transcript-driven session status: a TTY panel hosts the real TUI, so there is no event...
    private static readonly TimeSpan BusySafetyTimeout = TimeSpan.FromSeconds(120);
    // AC-276: long enough to cover the gap between a turn ending and the CLI stating how many sub-agents are still
    // running (measured p99 2634 ms), so the session does not flash Done — and fire "session finished" — in between.
    private static readonly TimeSpan TurnSettleDelay = TimeSpan.FromSeconds(3);
    private readonly TtyActivityStatusTracker _statusTracker = new(BusySafetyTimeout, TurnSettleDelay);

    // Throttles the pty-output liveness keep-alive (AC-75) to ~1 Hz — the terminal flushes at up to 30 fps.
    private DateTimeOffset _lastAliveSignalAt;
    private CancellationTokenSource? _statusTailCancellation;
    private DispatcherTimer? _statusPollTimer;
    private bool _statusTrackingStopped;

    // Raised once both the launch is configured and the view is subscribed; the view supplies the terminal size and wires the returned pty.
    public event Action<TtyLaunchRequest>? LaunchRequested;

    // Raised once a push-to-talk hold finished transcribing (no cleanup applied — TTY is a raw
    // keystroke stream, so a cleaned-up transcript with different wording would be actively wrong).
    // The view writes the text as raw bytes to the pty's stdin, the same path as a typed keystroke.
    public event Action<string>? VoiceTranscriptReady;

    // Status now lives on the shared SessionPanelViewModel base (AC-37), read by the one SessionHeaderBar.

    // AC-1013: Where this session's provider drops its statusline snapshots, set by the view once the pty...
    public string? StatusFile { get; private set; }

    // One-line render diagnostics (OS, terminal grid, display scale, locale) shown in the TTY header — surfaced so a remote/misrendering machine can be inspected without shell access. Set by the view, which owns the terminal/pty.
    [ObservableProperty]
    private string _diagnostics = string.Empty;

    // AC-34: true while an agent is coupled to this pane through the terminal-access MCP — drives the "agent connected" bar and its Disconnect button. The counterpart to both the human and the agent being able to type: it must always be visible that an agent is on the pane.
    [ObservableProperty]
    private bool _agentConnected;

    // AC-34: whether the coupled agent was approved to type, not only to read. Only drives `AgentDisconnectTip` — a Disconnect on a watching agent must not promise to interrupt something it never started.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentDisconnectTip))]
    private bool _agentCanType;

    // What the bar's Disconnect promises, which differs by what the operator approved: a watching agent has nothing running to interrupt, and a Ctrl-C there would land on the operator's own command.
    public string AgentDisconnectTip => AgentCanType
        ? "Stop the agent driving this terminal: interrupt whatever it is running (Ctrl-C) and break the connection immediately."
        : "Stop the agent reading this terminal: it loses access immediately. Nothing running is interrupted — this agent was never allowed to type.";

    // The label on the agent-connected bar ("Agent connected — &lt;session&gt;"), or null when no agent is coupled.
    [ObservableProperty]
    private string? _agentConnectedLabel;

    // The working directory the `claude` TUI runs in (the configured `Claude:WorkingDirectory`, else the process cwd — same resolution as `ClaudeTtySessionProvider`), shown compactly in the header so it is clear which project a session is operating on.
    [ObservableProperty]
    private string _workingPath = string.Empty;

    // AC-1013: Global TTY terminal font family (#40), mirrored from `CockpitViewModel.TerminalFontFamily` at
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    // Global TTY terminal font size in points (#40); same mirror/live-push wiring as `TerminalFontFamily`.
    [ObservableProperty]
    private int _terminalFontSize = 13;

    // AC-1013: Mirrors `CockpitViewModel.StackSessionsVertically` (#24), the multi-session grid's
    [ObservableProperty]
    private bool _isVerticalLayout;

    // AC-1013: This pane runs a plain shell, not an agent CLI (#AC-25). Bound in `TtyView.axaml` to gate...
    [ObservableProperty]
    private bool _isTerminal;

    // ContextUsedPercent, RateLimits and LimitsTooltip now live on the shared SessionPanelViewModel base (AC-37):
    // the TTY session feeds the base ContextUsedPercent and rebuilds the base RateLimits (5h/wk with reset times)
    // from the statusline relay, so the one SessionHeaderBar control renders its usage pill the same as the SDK one.

    private CancellationTokenSource? _limitsPollCancellation;

    private bool _hasOutstandingBackgroundShells;

    public override bool HasOutstandingBackgroundShells => _hasOutstandingBackgroundShells;

    // Tokens seen since the last completed turn (AC-398): a tool-using turn writes several assistant lines
    // before its own TurnComplete line, each with its own usage, and these hold the running sum until that line
    // arrives — see _AccumulateTurnUsage.
    private int _pendingTurnInputTokens;
    private int _pendingTurnOutputTokens;
    private int _pendingTurnCacheReadTokens;
    private int _pendingTurnCacheCreationTokens;

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

    // AC-1013: Design-time preview of a plain terminal pane (#AC-25/#AC-29) for the Screenshotter: the shared
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
        IOptions<CockpitOptions>? options = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null,
        VoiceOverlayCoordinator? voiceOverlay = null)
        : base(usageHistory)
    {
        _launcher = launcher;
        _providerResolver = providerResolver;
        _transcriptReader = transcriptReader;
        KindLabel = "TTY";
        WorkingPath = ResolveWorkingPath(options);
        // Also publish it on the shared base so the read/observe surface reports where this session runs — the
        // TTY working dir is known up front (unlike an SDK session, which learns it from its init event).
        WorkingDirectory = WorkingPath;
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState, voiceOverlay);
    }

    // The effective TTY working directory — the configured Claude:WorkingDirectory when set, else the process
    // cwd. Mirrors ClaudeTtySessionProvider's own resolution so the header shows exactly where the TUI runs.
    private static string ResolveWorkingPath(IOptions<CockpitOptions>? options)
    {
        var configured = options?.Value.Claude.WorkingDirectory;
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    // AC-1013: No cleanup — the terminal has no input box to proofread in, so the text goes straight to...
    protected override void OnVoiceTextReady(string text)
    {
        var typed = _AsTypedText(text);
        _lastTypedLength = typed.Length;
        if (typed.Length > 0)
        {
            VoiceTranscriptReady?.Invoke(typed);
        }
    }

    // How much text the CR is about to submit: `OnVoiceSubmitRequested` carries no parameter, and the beat it waits
    // has to cover the paste that `OnVoiceTextReady` just wrote (AC-993).
    private int _lastTypedLength;

    // AC-1013: Auto-submit: writes a carriage return into the pty, the same byte a physical Enter sends...
    protected override void OnVoiceSubmitRequested()
    {
        _scheduleAutoSubmit(AutoSubmitDelay(_lastTypedLength), () => VoiceTranscriptReady?.Invoke("\r"));
    }

    // A TTY session takes nothing here (AC-86): the text snapshot already reached the agent on the verify tool
    // result, and a pty carries no image. Typing the multi-line snapshot into the pty would let its embedded line
    // breaks submit as stray Enters, so the screenshot is simply dropped for a TTY. Returns false — nothing shown.
    public override Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);


    // AC-1013: Spills a captured screenshot to a file and pastes its path into the TUI (AC-341) — which is...
    protected override async Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng)
    {
        if (PasteTextAsync is null)
        {
            return NoOneToPasteInto;
        }

        string path;
        try
        {
            path = await _SpillAsync(screenshotPng);
        }
        catch (Exception)
        {
            // A full or read-only temp directory is the operator's to fix, and pasting a path to a file that is
            // not there would have the agent report a missing file instead — an error about the wrong thing.
            return "The screenshot could not be written to a temporary file, so it was not handed over.";
        }

        // AC-1013: Read again rather than reuse what the check above saw. Writing the file is a real await,...
        if (PasteTextAsync is not { } paste)
        {
            _TryDelete(path);
            return NoOneToPasteInto;
        }

        // Awaited, not fired and forgotten: reporting success before the paste has happened lets the caller
        // release its one-capture-at-a-time guard too early, and the two captures would then race into the same
        // prompt.
        await paste(path);
        return null;
    }

    private const string NoOneToPasteInto = "This terminal session is not on screen, so there is nothing to paste into.";

    // Where this session's captures are spilled so the agent can read them: under the OS temp directory, in a
    // folder of ours so the files are recognisable as the cockpit's. Per session rather than static, so a test
    // can point one at a directory it owns without reaching into what every other session is using.
    internal string SpillDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cockpit-screenshots");

    // How long a spilled capture is kept. It has to outlive the paste by a wide margin — the agent reads the
    // file when it gets round to the prompt, which is after the operator has typed the sentence that goes with
    // it — so this is about not keeping them forever, not about reclaiming space promptly.
    private static readonly TimeSpan SpillRetention = TimeSpan.FromDays(1);

    // Writes the capture where the agent can pick it up and returns the path, clearing out captures old enough
    // that nothing can still be waiting on them. Screenshots are exactly the thing this surface gives the
    // operator a redaction tool for, so leaving every one of them lying about indefinitely is not neutral.
    private async Task<string> _SpillAsync(byte[] screenshotPng)
    {
        _CreateSpillDirectory();
        _PruneSpentSpills();

        var path = Path.Combine(SpillDirectory, $"screenshot-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, screenshotPng);

        return path;
    }

    private void _CreateSpillDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(SpillDirectory);
            return;
        }

        // Owner-only, because the temp directory is shared on Unix and a capture holds whatever was on the
        // operator's screen. Only applied when the directory is created; an existing one keeps its mode.
        Directory.CreateDirectory(SpillDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void _PruneSpentSpills()
    {
        var spentBefore = DateTime.UtcNow - SpillRetention;
        try
        {
            foreach (var spent in new DirectoryInfo(SpillDirectory).EnumerateFiles("screenshot-*.png").Where(file => file.LastWriteTimeUtc < spentBefore))
            {
                try
                {
                    spent.Delete();
                }
                catch (Exception)
                {
                    // Per file rather than per round: an agent reading one of these holds it open, and one
                    // stubborn file must not stop the rest from going. It will be tried again next capture.
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping is not the job here: a directory that turned read-only, or went missing under us,
            // must not cost the operator the screenshot they just took.
        }
    }

    private static void _TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Same reasoning as the prune: the capture is already not being handed over, and failing to tidy up
            // after it is not something to report on top of that.
        }
    }

    // AC-1013: Asks the view to paste text into the terminal, the way the control does it for any other...
    public Func<string, Task>? PasteTextAsync { get; set; }

    private Action<TimeSpan, Action> _scheduleAutoSubmit = _DelayAutoSubmitOnUiThread;

    // Test seam (AC-64): run the auto-submit action inline instead of after the UI-thread gap, so the transcript-then-CR ordering — and, since AC-993, the gap chosen for it — is assertable without a real timer.
    internal void SetAutoSubmitScheduler(Action<TimeSpan, Action> scheduler) => _scheduleAutoSubmit = scheduler;

    // AC-1013: The gap between a pasted text and its submitting CR (AC-64, scaled in AC-993). A timer...
    internal static TimeSpan AutoSubmitDelay(int pastedLength) =>
        TimeSpan.FromMilliseconds(Math.Min(1000, 60 + pastedLength / 4));

    // The default gap that keeps the CR out of the transcript's ConPTY read (AC-64): a one-shot UI-thread timer.
    private static void _DelayAutoSubmitOnUiThread(TimeSpan delay, Action submit) =>
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(submit, delay));

    // AC-1013: Configures the panel with the profile and start defaults chosen in the New-session dialog,...
    public void LaunchConfigured(
        SessionProfile? profile,
        string? permissionMode,
        string? model,
        string? effort,
        string? workingDirectory = null,
        SessionResume? resume = null,
        IReadOnlyDictionary<string, string>? pluginOptions = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        SessionResources? contributed = null)
    {
        _configuredProfile = profile;
        _configuredResume = resume;
        // AC-563: the header's MCP hover lives on the shared bar, so this route merges its selection here too —
        // without it a terminal pane reports an unknown selection while its profile had named one. What the
        // launch really mounts replaces it the moment the route reports (AC-927).
        McpServerSelection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);
        _configuredContributed = contributed;
        _configuredWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        // Show and publish the effective working directory: the per-session override when given, else the
        // global resolution. Keeps the header and the read/observe surface pointing where the TUI actually runs.
        if (_configuredWorkingDirectory is not null)
        {
            WorkingPath = _configuredWorkingDirectory;
            WorkingDirectory = _configuredWorkingDirectory;
        }
        // AC-1013: Read-aloud and status both tail this session's transcript through the generic reader...
        _transcriptBaseline = _transcriptReader?.SnapshotTranscripts(profile);
        _configuredPermissionMode = permissionMode;
        _configuredModel = model;
        _configuredEffort = effort;
        _configuredPluginOptions = pluginOptions;
        _isLaunchConfigured = true;
        ActiveProfileLabel = profile?.Label;
        Status = profile is null ? "Launching TUI..." : $"Launching TUI ({profile.Label})...";
        SessionStatus = SessionStatus.Busy;

        // AC-410: RestoreOffer is still set here for a restored pane's first launch — the caller
        // (CockpitViewModel._StartRestoredSessionAsync) only clears it once this call returns, which is before the
        // pty has actually spawned. A fresh (never-restored) session has no offer, so this is a no-op there.
        _degradeInsteadOfCloseOnExit = RestoreOffer is not null;
        _restoredOfferSnapshot = RestoreOffer;

        TryRaiseLaunch();
    }

    // AC-1013: Configures this panel as a plain terminal running `shell` (#AC-25), reusing the whole TTY
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

    // Raises `LaunchRequested` once both the profile is configured and the view is
    // subscribed. Called from both sides — the dialog result and the view's subscription — so whichever
    // happens second fires it; the guard makes it launch exactly once.
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
        var launchOptions = _LaunchOptions();

        // AC-661: the same cap TtyLauncher hands the OS, so the bar can warn on the approach to it.
        MemoryCapBytes = SessionMemoryCap.ResolveBytes(_configuredProfile, launchOptions);

        LaunchRequested.Invoke(new TtyLaunchRequest(
            _launcher,
            provider,
            _configuredProfile,
            launchOptions,
            _configuredWorkingDirectory,
            _configuredResume,
            McpServerSelection,
            _configuredContributed,
            // AC-218: set on the panel by CockpitViewModel before LaunchConfigured, so it is already current here.
            ProjectId));
    }

    // AC-1013: The start defaults in the provider's vocabulary. A blank knob is left out rather than...
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

    // AC-1013: Called by the view when the hosted TUI process exits after running (the user closed claude...
    public void OnProcessExited(string? lastOutput = null)
    {
        _StopStatusTracking();
        Status = "TUI process exited.";
        SessionStatus = SessionStatus.Done;

        if (_degradeInsteadOfCloseOnExit && _restoredOfferSnapshot is { } offer)
        {
            _degradeInsteadOfCloseOnExit = false;
            _restoredOfferSnapshot = null;
            RestoreOffer = offer with
            {
                Availability = SessionRestoreAvailability.Gone,
                Explanation = _DegradedExitExplanation(lastOutput),
            };
            return;
        }

        RaiseCloseRequested();
    }

    // What the degraded restore offer's banner shows for why the earlier conversation is gone — the terminal's own last words rather than a guess, since nothing here can name the actual cause (AC-410).
    private static string _DegradedExitExplanation(string? lastOutput) =>
        string.IsNullOrWhiteSpace(lastOutput)
            ? "Claude exited immediately instead of resuming, before anything was printed."
            : $"Claude exited immediately instead of resuming:\n{lastOutput.Trim()}";

    // AC-1013: Called by the view once the pty has actually spawned, so the header stops reading "Launching
    public void OnLaunchSucceeded()
    {
        Status = "Running";
        SessionStatus = SessionStatus.Idle;
        _degradeInsteadOfCloseOnExit = false;
        _restoredOfferSnapshot = null;
        // Re-seeded here rather than left at construction: a restored/isolated pane can sit configured for a
        // while (waiting on a worktree, on the operator) before its TUI actually comes up, and that wait is not
        // working time (AC-398, mirrors SessionViewModel's own _startedAt).
        _startedAt = DateTimeOffset.Now;
        _StartStatusTracking();
    }

    // Called by the view when the TUI could not be launched: the panel stays (the error is shown in the terminal) instead of auto-closing.
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
        // StatusFile is already set: the view wires it the moment the pty is up, which is before it tells us the
        // launch succeeded — the one call that gets us here.
        _ = _TailTranscriptForStatusAsync(_configuredProfile, _transcriptBaseline, StatusFile, _statusTailCancellation.Token);

        _statusPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusPollTimer.Tick += _OnStatusPollTick;
        _statusPollTimer.Start();
    }

    // Classifies each appended transcript line (busy / done / metadata) and feeds it to the tracker; the tailer runs on a background task, so the status update is marshaled onto the UI thread.
    private async Task _TailTranscriptForStatusAsync(SessionProfile? profile, IReadOnlySet<string> transcriptBaseline, string? statusFile, CancellationToken cancellationToken)
    {
        if (_transcriptReader is null)
        {
            return;
        }

        try
        {
            await foreach (var reading in _transcriptReader.ReadActivityAsync(profile, transcriptBaseline, statusFile, cancellationToken))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_statusTrackingStopped)
                    {
                        SessionStatus = _statusTracker.OnActivity(reading.Activity, DateTimeOffset.UtcNow);
                    }

                    // A backgrounded shell does not hold the status (it may be a dev server that never ends), but it
                    // does withhold the "session finished" notification — see HasOutstandingBackgroundShells (AC-276).
                    _hasOutstandingBackgroundShells = reading.OutstandingShells > 0;
                    OnPropertyChanged(nameof(HasOutstandingBackgroundShells));

                    // AC-1013: Exposes the raw transcript line for substring-scanning read/observe consumers.
                    if (reading.RawLine is { } line)
                    {
                        RaiseOutputText(line);
                    }

                    // AC-398/AC-1013: Holds tool-turn usage pending so one turn is not counted as many messages.
                    if (reading.Usage is { } usage)
                    {
                        _pendingTurnInputTokens += usage.InputTokens;
                        _pendingTurnOutputTokens += usage.OutputTokens;
                        _pendingTurnCacheReadTokens += usage.CacheReadInputTokens;
                        _pendingTurnCacheCreationTokens += usage.CacheCreationInputTokens;
                    }

                    // AC-1013: RawLine is null for the reader's synthetic keep-alive readings (a background sub-agent's
                    if (reading.Activity == SessionActivity.TurnComplete && reading.RawLine is not null)
                    {
                        _AccumulateTurnUsage();
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
            // AC-1013: A transient IO fault while tailing (the JSONL file momentarily locked, a read error) must
            Dispatcher.UIThread.Post(_StopStatusTracking);
        }
    }

    // AC-1013: The last `count` rows this session has written, oldest first, with the total the record...
    public SessionTranscriptSlice ReadTranscriptEntries(int count) =>
        _transcriptReader?.ReadEntries(_configuredProfile, StatusFile, count) ?? SessionTranscriptSlice.Empty;

    // AC-294: whether this session has a record that can be read back at all — the two things
    // `ReadTranscriptEntries` needs to name one. False for a provider with no reader (Codex tails activity but
    // reads nothing back) and before the pty is up, which is what sets `StatusFile`.

    // Asks the route, not the content: a session that has a record and has written nothing to it yet is exactly
    // the one `stuck` exists for, so emptiness must not be the thing that refuses a watch on it.
    public bool HasReadableTranscript => _transcriptReader is not null && StatusFile is not null;

    private void _OnStatusPollTick(object? sender, EventArgs e)
    {
        if (_statusTrackingStopped)
        {
            return;
        }

        SessionStatus = _statusTracker.Poll(DateTimeOffset.UtcNow);
    }

    // AC-1013: Folds the tokens accumulated since the previous turn into the session meter (AC-398) and...
    private void _AccumulateTurnUsage()
    {
        var usage = new TokenUsage(
            _pendingTurnInputTokens, _pendingTurnOutputTokens, _pendingTurnCacheReadTokens, _pendingTurnCacheCreationTokens);
        _pendingTurnInputTokens = 0;
        _pendingTurnOutputTokens = 0;
        _pendingTurnCacheReadTokens = 0;
        _pendingTurnCacheCreationTokens = 0;

        _usage.Add(usage, costUsd: null);
        HasUsage = _usage.HasData;
        UsageSummary = _usage.Summary;
        UsageTooltip = _usage.Tooltip;
        _RecordUsageSnapshot();
    }

    // AC-1013: Writes the running totals to the usage trail after every turn (AC-398), same as the SDK path
    private protected override (UsageRunKind RunKind, string? RunId, string? RunLabel, string? Model) GetUsageSnapshotMetadata() =>
        (UsageRunKind.Interactive, null, null, _configuredModel);

    // The transcript callback and disposal both run on the UI thread, so a queued callback can replace the write while drain awaits.
    private protected override bool UsageWritesMayBeQueuedDuringDrain => true;

    // AC-1013: The pty produced output — the TUI is still drawing (a thinking spinner ticking, text...
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

    // AC-1013: Where a prompt goes when something other than the operator sends one (a scheduled resume,...
    public Action<string>? PromptSink
    {
        get => _promptSink;
        set
        {
            _promptSink = value;
            if (value is not null)
            {
                DeliverHeldPrompt();
            }
        }
    }

    private Action<string>? _promptSink;

    // AC-760: `PromptSink` turns non-null as soon as the pty exists, well before the CLI actually reads stdin;
    // `TtyView` measures that gap (bracketed paste / a fallback deadline) and reports it via `MarkHostedTuiReady`.
    // `ResetHostedTuiReadiness` clears it so a relaunched pane does not inherit "ready" from the session before it.
    private bool _hostedTuiReady;

    // Called by the view once, from the single place its own readiness answer turns true — mirrors how the
    // `PromptSink` setter above is the one place *its* answer changes. Delivers a brief that was held waiting on
    // exactly this, the same way assigning `PromptSink` already does.
    public void MarkHostedTuiReady()
    {
        if (_hostedTuiReady)
        {
            return;
        }

        _hostedTuiReady = true;
        DeliverHeldPrompt();
    }

    // Called by the view right before it launches a new pty into a pane it is reusing, so the gate above starts
    // closed again rather than carrying over the previous session's answer.
    public void ResetHostedTuiReadiness() => _hostedTuiReady = false;

    // The pty sink is necessary but not sufficient: it exists once the process is spawned, but a prompt written to
    // it before the CLI reads stdin in raw mode is the exact failure AC-760 reports — text lands, Enter does not.
    public override bool CanTakeAPrompt => PromptSink is not null && _hostedTuiReady;

    public override Task<bool> SendPromptAsync(string prompt)
    {
        if (PromptSink is not { } sink)
        {
            return Task.FromResult(false);
        }

        // A TUI takes a prompt the way a person gives one: the text, then Enter. Without the newline it sits in
        // the composer looking sent, which is the failure that looks like success. The prompt itself is typed only —
        // the trailing carriage return is the one byte here that is meant to act as a key.
        sink(_AsTypedText(prompt) + "\r");

        // AC-1013: Told rather than waited for. This pane's status is otherwise inferred from what the CLI...
        SessionStatus = _statusTracker.OnActivity(SessionActivity.Busy, DateTimeOffset.UtcNow);

        return Task.FromResult(true);
    }

    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    // AC-1013: Text on its way into a pty, reduced to what a person could type into the composer: every...
    private static string _AsTypedText(string text)
    {
        var typed = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == Escape)
            {
                index = _EndOfEscapeSequence(text, index);
                continue;
            }

            // A space rather than nothing, so the words either side of a dropped line break stay apart.
            typed.Append(char.IsControl(text[index]) ? ' ' : text[index]);
        }

        // Collapse the runs of spaces that leaves, so a prompt written over a few lines reads as the one sentence it
        // was meant to be.
        return string.Join(' ', typed.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // AC-1013: The index of the last character of the escape sequence that starts at start, so the...
    private static int _EndOfEscapeSequence(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return text.Length - 1;
        }

        switch (text[start + 1])
        {
            // CSI (ESC [) runs over parameter and intermediate bytes until a final byte in 0x40-0x7E.
            case '[':
            {
                var index = start + 2;
                while (index < text.Length && text[index] is >= ' ' and < '@')
                {
                    index++;
                }

                return Math.Min(index, text.Length - 1);
            }

            // The string-payload family (OSC, DCS, SOS, PM, APC) runs until a BEL or an ESC \.
            case ']' or 'P' or 'X' or '^' or '_':
                return _EndOfEscapeString(text, start + 2);

            // A charset designator (ESC ( B) takes one character more than a two-character escape does.
            case '(' or ')' or '*' or '+':
                return Math.Min(start + 2, text.Length - 1);

            default:
                return start + 1;
        }
    }

    private static int _EndOfEscapeString(string text, int from)
    {
        for (var index = from; index < text.Length; index++)
        {
            if (text[index] == Bell)
            {
                return index;
            }

            if (text[index] == Escape && index + 1 < text.Length && text[index + 1] == '\\')
            {
                return index + 1;
            }
        }

        return text.Length - 1;
    }

    // AC-1013: Starts reading this session's usage from the file the provider plugin's statusline writes,...
    public void TrackLimits(
        string? statusFile,
        IReadOnlyList<PluginUsageSignal> signals,
        Func<string, IReadOnlyList<PluginUsageReading>>? readUsage)
    {
        // Kept whatever else this call decides (AC-609). The file is this session's own name for itself — it is
        // what the transcript reader identifies its record by, and that matters just as much for a provider that
        // declares no usage signals as for one that does.
        StatusFile = statusFile;

        if (string.IsNullOrWhiteSpace(statusFile) || readUsage is null || signals.Count == 0)
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
                        if (File.Exists(statusFile))
                        {
                            var readings = readUsage(await File.ReadAllTextAsync(statusFile, cancellation));
                            if (readings.Count > 0)
                            {
                                // AC-1013: AC-577, no fast path — deliberately. This loop lives inside Task.Run, so
                                await Dispatcher.UIThread.InvokeAsync(() => ApplyUsage(signals, readings));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // A file caught mid-rename, a session that just ended, a reader that choked on a snapshot
                        // written half-way. The next tick sorts it out; a status bar must never be a reason for a
                        // session to fall over.
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false);
                }
            },
            cancellation);
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        // AC-1013: The terminal control owns the pty lifetime (it created it via the launcher); it disposes
        _limitsPollCancellation?.Cancel();
        _limitsPollCancellation?.Dispose();
        _limitsPollCancellation = null;
        _StopStatusTracking();

        // A turn interrupted mid-flight (the operator closes the pane, or the CLI is killed, before its
        // terminating end_turn line arrives) would otherwise lose its tokens entirely rather than fold into the
        // next turn's total that never comes — flush whatever was pending as this pane's own last, partial turn.
        if (_pendingTurnInputTokens > 0 || _pendingTurnOutputTokens > 0
            || _pendingTurnCacheReadTokens > 0 || _pendingTurnCacheCreationTokens > 0)
        {
            _AccumulateTurnUsage();
        }

        // AC-1013: Let the last turn's usage write land (AC-398, mirrors SessionViewModel.DisposeCoreAsync) —...
        await _DrainUsageWritesAsync();

        // AC-1013: Dropped here rather than left to the view, which is not told when a session closes: the...
        PasteTextAsync = null;

        // AC-1013: The prompt route goes with it, and for the identical reason. It points at the view's...
        PromptSink = null;
    }
}
