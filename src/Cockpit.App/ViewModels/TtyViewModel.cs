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
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using Cockpit.Core.Usage;

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
    private IReadOnlySet<string>? _configuredEnabledMcpServerNames;
    private SessionResources? _configuredContributed;
    private bool _launched;

    /// <summary>
    /// True from <see cref="LaunchConfigured"/> until <see cref="OnLaunchSucceeded"/> for the first launch of a
    /// restored pane, false otherwise (AC-410). Armed off <see cref="SessionPanelViewModel.RestoreOffer"/> being set
    /// at configure time — <c>CockpitViewModel._StartRestoredSessionAsync</c> only clears it after the start call
    /// returns, but that happens as soon as this launch is configured, well before the pty has actually spawned, so
    /// <c>RestoreOffer</c> itself cannot be read again once the process later exits; <see cref="_restoredOfferSnapshot"/>
    /// is what survives that gap. While armed, <see cref="OnProcessExited"/> must not close the pane: a resume that
    /// fails fast (an expired conversation id) would otherwise erase the very pane record it was trying to bring
    /// back, in the same run that just restored it.
    /// </summary>
    private bool _degradeInsteadOfCloseOnExit;

    /// <summary>The offer this pane was restored with, captured when <see cref="_degradeInsteadOfCloseOnExit"/> armed — the source a failed exit degrades back to, since <see cref="SessionPanelViewModel.RestoreOffer"/> is already null by then.</summary>
    private SessionRestorePlan? _restoredOfferSnapshot;

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
    // AC-276: long enough to cover the gap between a turn ending and the CLI stating how many sub-agents are still
    // running (measured p99 2634 ms), so the session does not flash Done — and fire "session finished" — in between.
    private static readonly TimeSpan TurnSettleDelay = TimeSpan.FromSeconds(3);
    private readonly TtyActivityStatusTracker _statusTracker = new(BusySafetyTimeout, TurnSettleDelay);

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

    /// <summary>AC-34: whether the coupled agent was approved to type, not only to read. Only drives <see cref="AgentDisconnectTip"/> — a Disconnect on a watching agent must not promise to interrupt something it never started.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentDisconnectTip))]
    private bool _agentCanType;

    /// <summary>What the bar's Disconnect promises, which differs by what the operator approved: a watching agent has nothing running to interrupt, and a Ctrl-C there would land on the operator's own command.</summary>
    public string AgentDisconnectTip => AgentCanType
        ? "Stop the agent driving this terminal: interrupt whatever it is running (Ctrl-C) and break the connection immediately."
        : "Stop the agent reading this terminal: it loses access immediately. Nothing running is interrupted — this agent was never allowed to type.";

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
    /// <para>
    /// It also decides whether an agent may be offered this pane through the terminal-access MCP (AC-34): the pane
    /// registers with this value, and only a shell is listed, resolvable and couplable. So a change that lets this
    /// turn true for an agent session opens another session's transcript to an agent — treat it as a gate, not
    /// only as a chrome flag.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isTerminal;

    // ContextUsedPercent, RateLimits and LimitsTooltip now live on the shared SessionPanelViewModel base (AC-37):
    // the TTY session feeds the base ContextUsedPercent and rebuilds the base RateLimits (5h/wk with reset times)
    // from the statusline relay, so the one SessionHeaderBar control renders its usage pill the same as the SDK one.

    private CancellationTokenSource? _limitsPollCancellation;

    /// <summary>Running token total for the session (AC-398), folded from every transcript reading that carried usage — see <see cref="SessionTranscriptActivity.Usage"/>.</summary>
    private readonly SessionUsageMeter _usage = new();

    private bool _hasOutstandingBackgroundShells;

    /// <inheritdoc />
    public override bool HasOutstandingBackgroundShells => _hasOutstandingBackgroundShells;

    /// <summary>When this session's TUI actually came up, seeded again in <see cref="OnLaunchSucceeded"/> — mirrors <c>SessionViewModel</c>'s own <c>_startedAt</c> so a persisted snapshot measures working time, not launch setup.</summary>
    private DateTimeOffset _startedAt = DateTimeOffset.Now;

    /// <summary>The most recent write to the usage trail, awaited on teardown — same reasoning as <c>SessionViewModel._pendingUsageWrite</c>.</summary>
    private Task? _pendingUsageWrite;

    /// <summary>Where the running totals are kept so they outlive the session (AC-398). Null in the design-time graph and in tests built without one.</summary>
    private readonly IUsageHistory? _usageHistory;

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
        IOptions<CockpitOptions>? options = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null)
    {
        _launcher = launcher;
        _providerResolver = providerResolver;
        _transcriptReader = transcriptReader;
        _usageHistory = usageHistory;
        KindLabel = "TTY";
        WorkingPath = ResolveWorkingPath(options);
        // Also publish it on the shared base so the read/observe surface reports where this session runs — the
        // TTY working dir is known up front (unlike an SDK session, which learns it from its init event).
        WorkingDirectory = WorkingPath;
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, cleanupService, openMicState);
    }

    // The effective TTY working directory — the configured Claude:WorkingDirectory when set, else the process
    // cwd. Mirrors ClaudeTtySessionProvider's own resolution so the header shows exactly where the TUI runs.
    private static string ResolveWorkingPath(IOptions<CockpitOptions>? options)
    {
        var configured = options?.Value.Claude.WorkingDirectory;
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    // The text from the OnVoiceTextReady that precedes a submit (AC-120) — a voice-hold transcript, or text injected
    // via InjectAndSubmit/InjectVoiceTranscript (AC-152, plugin SendToSession, an embedded Autopilot step's opening
    // brief), which this base already treats as "the same per-kind path as a finished voice transcript". Carried
    // here because OnVoiceSubmitRequested has no text parameter of its own to hand SpeakTurnAcknowledgmentAsync.
    private string? _lastVoiceText;

    /// <summary>
    /// No cleanup — the terminal has no input box to proofread in, so the text goes straight to the pty like a typed
    /// keystroke. Typed is all it may be: it is reduced to <see cref="_AsTypedText"/> first, so nothing in it can act
    /// as a key. Sending stays its own gesture — the operator's Enter, or <see cref="OnVoiceSubmitRequested"/>'s
    /// carriage return. Text that was nothing but keys writes nothing at all.
    /// </summary>
    protected override void OnVoiceTextReady(string text)
    {
        _lastVoiceText = text;
        var typed = _AsTypedText(text);
        if (typed.Length > 0)
        {
            VoiceTranscriptReady?.Invoke(typed);
        }
    }

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
    /// <para>
    /// Also speaks the turn-start acknowledgement (AC-120, follow-up on AC-99's SDK-only wiring) for whatever text
    /// just landed — matching the SDK session, which speaks it for every dispatched turn regardless of origin.
    /// <see cref="SessionPanelViewModel.SpeakTurnAcknowledgmentAsync"/> itself no-ops unless read-aloud + a
    /// non-Off ack mode are on, so this is safe to fire unconditionally.
    /// </para>
    /// </summary>
    protected override void OnVoiceSubmitRequested()
    {
        if (_lastVoiceText is { } voiceText)
        {
            _lastVoiceText = null;
            _ = SpeakTurnAcknowledgmentAsync(voiceText);
        }

        _scheduleAutoSubmit(() => VoiceTranscriptReady?.Invoke("\r"));
    }

    /// <summary>
    /// A TTY session takes nothing here (AC-86): the text snapshot already reached the agent on the verify tool
    /// result, and a pty carries no image. Typing the multi-line snapshot into the pty would let its embedded line
    /// breaks submit as stray Enters, so the screenshot is simply dropped for a TTY. Returns false — nothing shown.
    /// </summary>
    public override Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);


    /// <summary>
    /// Spills a captured screenshot to a file and pastes its path into the TUI (AC-341) — which is what the TUI
    /// wanted all along, and the clipboard was only ever a way of getting there.
    /// </summary>
    /// <remarks>
    /// A pty carries bytes, and no byte sequence means "here is an image" to a program reading one. What the
    /// agents running in these sessions do understand is a path: <c>claude</c> reads the file and attaches it. The
    /// route used to go the long way round — put the image on the system clipboard, press the terminal's paste
    /// key, and let the terminal write the image to a temp file and paste <em>that</em> path. Every step after the
    /// first was already this. So the clipboard bought nothing and cost the operator whatever they had copied —
    /// a trade Raymond declined outright when he saw it (2026-07-27): the capture should reach the session, and
    /// preferably not by way of the clipboard at all.
    /// <para>
    /// It is also the reason this now works the same everywhere. The clipboard route had to negotiate image
    /// formats with three different windowing systems and got it wrong on Windows for a fortnight; a file has no
    /// formats to negotiate.
    /// </para>
    /// <para>
    /// Deliberately not submitted afterwards, the same as the chat session: the path lands in the TUI's own
    /// prompt, and the sentence that goes with the screenshot is the operator's to type.
    /// </para>
    /// </remarks>
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

        // Read again rather than reuse what the check above saw. Writing the file is a real await, and the
        // operator can close the session while it runs — <see cref="DisposeCoreAsync"/> clears this exactly so
        // that a capture landing afterwards does not report success into a terminal that is gone. Holding the
        // delegate across the await would defeat that; the check above only saves the file write.
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

    /// <summary>
    /// Where this session's captures are spilled so the agent can read them: under the OS temp directory, in a
    /// folder of ours so the files are recognisable as the cockpit's. Per session rather than static, so a test
    /// can point one at a directory it owns without reaching into what every other session is using.
    /// </summary>
    internal string SpillDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cockpit-screenshots");

    /// <summary>
    /// How long a spilled capture is kept. It has to outlive the paste by a wide margin — the agent reads the
    /// file when it gets round to the prompt, which is after the operator has typed the sentence that goes with
    /// it — so this is about not keeping them forever, not about reclaiming space promptly.
    /// </summary>
    private static readonly TimeSpan SpillRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// Writes the capture where the agent can pick it up and returns the path, clearing out captures old enough
    /// that nothing can still be waiting on them. Screenshots are exactly the thing this surface gives the
    /// operator a redaction tool for, so leaving every one of them lying about indefinitely is not neutral.
    /// </summary>
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

    /// <summary>
    /// Asks the view to paste text into the terminal, the way the control does it for any other paste.
    /// </summary>
    /// <remarks>
    /// A settable delegate rather than an event, for two reasons. It returns a task the injection awaits, which a
    /// multicast event cannot do meaningfully; and a session panel has exactly one view, so "one subscriber" is
    /// the truth rather than a restriction. The view clears it when it lets go, and <see cref="DisposeCoreAsync"/>
    /// clears it when the session closes — a stale delegate would let a capture that lands after the session is
    /// gone report success into nothing, which is the one outcome this whole path exists to prevent.
    /// </remarks>
    public Func<string, Task>? PasteTextAsync { get; set; }

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
    /// <paramref name="contributed"/> is what the plugins give this session (AC-165), or null for nothing
    /// contributed.
    /// </summary>
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
        _configuredEnabledMcpServerNames = enabledMcpServerNames;
        _configuredContributed = contributed;
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

        // AC-410: RestoreOffer is still set here for a restored pane's first launch — the caller
        // (CockpitViewModel._StartRestoredSessionAsync) only clears it once this call returns, which is before the
        // pty has actually spawned. A fresh (never-restored) session has no offer, so this is a no-op there.
        _degradeInsteadOfCloseOnExit = RestoreOffer is not null;
        _restoredOfferSnapshot = RestoreOffer;

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
            _configuredResume,
            _configuredEnabledMcpServerNames,
            _configuredContributed,
            // AC-218: set on the panel by CockpitViewModel before LaunchConfigured, so it is already current here.
            ProjectId));
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
    /// TUI, or it ended). A TTY panel is ordinarily a live terminal with nothing left to interact with once the
    /// process is gone, so this asks the cockpit to close the panel — mirrors closing claude itself.
    /// <para>
    /// AC-410's exception: within a restored pane's <see cref="_degradeInsteadOfCloseOnExit"/> window, an exit is
    /// not "the operator is done", it is a resume that failed before it even started (an expired conversation id
    /// makes <c>claude --resume</c> print an error and exit immediately). Closing there would delete the very pane
    /// record the operator was trying to bring back, at the exact moment it turns out to be needed. Instead the
    /// restore offer comes back with the failure visible, so "Start fresh" is still one click away.
    /// </para>
    /// </summary>
    /// <param name="lastOutput">
    /// The last visible terminal lines, for the degraded offer's explanation — null when there was nothing to
    /// capture, or when this exit is not within the degrade window and the lines go unused.
    /// </param>
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

    /// <summary>What the degraded restore offer's banner shows for why the earlier conversation is gone — the terminal's own last words rather than a guess, since nothing here can name the actual cause (AC-410).</summary>
    private static string _DegradedExitExplanation(string? lastOutput) =>
        string.IsNullOrWhiteSpace(lastOutput)
            ? "Claude exited immediately instead of resuming, before anything was printed."
            : $"Claude exited immediately instead of resuming:\n{lastOutput.Trim()}";

    /// <summary>
    /// Called by the view once the pty has actually spawned, so the header stops reading "Launching
    /// TUI..." while the real TUI is already interactive below it. Also starts JSONL-driven status
    /// tracking: the session is now idle-waiting-for-you until the transcript shows a turn in flight.
    /// <para>
    /// Closes a restored pane's degrade window (AC-410): a launch that got this far actually put something on
    /// screen, so an exit from here on is the operator closing claude, not a resume failing before it started.
    /// </para>
    /// </summary>
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

                    // A backgrounded shell does not hold the status (it may be a dev server that never ends), but it
                    // does withhold the "session finished" notification — see HasOutstandingBackgroundShells (AC-276).
                    _hasOutstandingBackgroundShells = reading.OutstandingShells > 0;
                    OnPropertyChanged(nameof(HasOutstandingBackgroundShells));

                    // Surface the raw transcript line to the read/observe surface: it carries any output signal
                    // (a pull-request url printed by gh, a merged/closed line) as a substring regardless of which
                    // JSONL field holds it, which is exactly what a substring-scanning watcher needs. A synthetic
                    // keep-alive reading (background sub-agent activity) has no line, so there is nothing to scan.
                    if (reading.RawLine is { } line)
                    {
                        RaiseOutputText(line);
                    }

                    // AC-398: held pending rather than folded straight in — a turn that used a tool writes several
                    // assistant lines before it completes, each with its own usage, and summing them under one
                    // Turns increment (rather than one per line) is what keeps that counter meaning "turns", not
                    // "assistant messages".
                    if (reading.Usage is { } usage)
                    {
                        _pendingTurnInputTokens += usage.InputTokens;
                        _pendingTurnOutputTokens += usage.OutputTokens;
                        _pendingTurnCacheReadTokens += usage.CacheReadInputTokens;
                        _pendingTurnCacheCreationTokens += usage.CacheCreationInputTokens;
                    }

                    // RawLine is null for the reader's synthetic keep-alive readings (a background sub-agent's
                    // activity re-emitted each poll, or the state it hands back once that agent stops) — those
                    // carry no usage and are not a second real turn ending, so flushing on them would write a
                    // duplicate row with the same totals and an inflated Turns count.
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
    /// Folds the tokens accumulated since the previous turn into the session meter (AC-398) and refreshes the
    /// bound meter text — mirrors <c>SessionViewModel._AccumulateUsage</c>, fed by the transcript tail instead of
    /// the SDK event stream. No cost: the CLI's on-disk transcript reports token usage per assistant message but
    /// never a cost figure, unlike the SDK path's stream-json <c>result</c> event — and the cockpit does not
    /// compute one itself from tokens (see <c>PluginModelCostEstimate</c>'s own doc: "the cockpit never works a
    /// figure out itself"). <see cref="SessionUsageMeter.TotalCostUsd"/> therefore simply stays at its default,
    /// which reads identically to a provider that reports no cost at all.
    /// </summary>
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

    /// <summary>
    /// Writes the running totals to the usage trail after every turn (AC-398), same as the SDK path
    /// (<c>SessionViewModel._RecordUsageSnapshot</c>) and for the same reason: recording only at the end would
    /// lose exactly the run that crashed. Not awaited — a turn settling must not wait on a file — but kept so
    /// <see cref="DisposeCoreAsync"/> can drain it before the pane goes away.
    /// </summary>
    private void _RecordUsageSnapshot()
    {
        if (_usageHistory is null || !_usage.HasData)
        {
            return;
        }

        _pendingUsageWrite = _usageHistory.RecordAsync(new UsageSnapshot
        {
            PaneId = PaneId,
            StartedAt = _startedAt,
            RecordedAt = DateTimeOffset.Now,
            // Always Interactive/no run: nothing embeds a TTY session today (CockpitViewModel.Embed only ever
            // builds an SDK SessionViewModel) — if that changes, this is where a run's id/label would come from.
            RunKind = UsageRunKind.Interactive,
            RunId = null,
            RunLabel = null,
            ProfileLabel = ActiveProfileLabel,
            Model = _configuredModel,
            InputTokens = _usage.InputTokens,
            OutputTokens = _usage.OutputTokens,
            CacheReadInputTokens = _usage.CacheReadInputTokens,
            CacheCreationInputTokens = _usage.CacheCreationInputTokens,
            TotalCostUsd = _usage.TotalCostUsd,
            Turns = _usage.Turns,
        });
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
    /// Where a prompt goes when something other than the operator sends one (a scheduled resume, AC-234): the same
    /// pty stdin the keystrokes go to. Set by the view once the terminal is launched, because the pty is the view's
    /// to own; null before that, and the session then reports it cannot take a prompt yet.
    /// </summary>
    public Action<string>? PromptSink { get; set; }

    /// <inheritdoc/>
    /// <remarks>The pty sink is the whole answer here: with one, a prompt is typed and submitted; without one, there is nowhere to type it.</remarks>
    public override bool CanTakeAPrompt => PromptSink is not null;

    /// <inheritdoc/>
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

        // Told rather than waited for. This pane's status is otherwise inferred from what the CLI prints, so between
        // pressing Enter and the first line that reads as work it would go on reporting itself standing still — long
        // enough for a second wake (AC-395) to be let through onto a session that is already answering the first.
        // Submitting a turn is the one moment the host knows about one without having to read it off the screen, and
        // it is said in the tracker's own terms so the safety timeout still decays it if nothing ever comes back.
        SessionStatus = _statusTracker.OnActivity(SessionActivity.Busy, DateTimeOffset.UtcNow);

        return Task.FromResult(true);
    }

    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    // Text on its way into a pty, reduced to what a person could type into the composer: every control byte becomes a
    // space, and escape sequences are dropped whole. A pty has no notion of "just text" — a carriage return in it is
    // Enter, a tab is completion, 0x03 is Ctrl+C, and an escape sequence drives the TUI and the emulator instead of
    // filling the composer. So text that nobody typed (a voice transcript, a scheduled resume, an issue body a plugin
    // handed over from a tracker anyone can write into) can fill the composer and do nothing else; sending it stays a
    // deliberate, separate act.
    //
    // The line layout of a multi-line prompt is the price, and it is the right one: the alternative is that its first
    // line submits itself and the rest arrives as input to whatever the session did next. Bracketed paste would keep
    // the layout, but whether the hosted TUI honours it is the TUI's choice, not ours — a filter that only sometimes
    // holds is not a filter.
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

    // The index of the last character of the escape sequence that starts at start, so the caller's own increment
    // lands just past it. An unterminated or unrecognised sequence swallows the rest of the text:
    // leaving the introducer behind is the one outcome worth avoiding, and text trailing an escape that never ends is
    // not text anybody typed. Whatever this misses still cannot reach the pty — the caller turns every remaining
    // control byte into a space, so a gap here costs legibility, never safety.
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

    /// <summary>
    /// Starts reading this session's usage from the file the provider plugin's statusline writes, interpreting it
    /// with that provider's own reader (AC-229) — the host polls, the plugin says what the contents mean.
    /// Polled rather than watched: the file is rewritten whole every few seconds by a shell script, and a
    /// filesystem watcher on a write-then-rename fires more often than it tells you anything.
    /// </summary>
    /// <param name="statusFile">Where the provider's statusline drops its snapshots; nothing is tracked without one.</param>
    /// <param name="signals">What the provider says its sessions can run out of, which names and describes each reading.</param>
    /// <param name="readUsage">The provider's reader, turning a snapshot's contents into readings.</param>
    public void TrackLimits(
        string? statusFile,
        IReadOnlyList<PluginUsageSignal> signals,
        Func<string, IReadOnlyList<PluginUsageReading>>? readUsage)
    {
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

        // A turn interrupted mid-flight (the operator closes the pane, or the CLI is killed, before its
        // terminating end_turn line arrives) would otherwise lose its tokens entirely rather than fold into the
        // next turn's total that never comes — flush whatever was pending as this pane's own last, partial turn.
        if (_pendingTurnInputTokens > 0 || _pendingTurnOutputTokens > 0
            || _pendingTurnCacheReadTokens > 0 || _pendingTurnCacheCreationTokens > 0)
        {
            _AccumulateTurnUsage();
        }

        // Let the last turn's usage write land (AC-398, mirrors SessionViewModel.DisposeCoreAsync) — after
        // stopping the status tail above, not before: that tail is what kicks the write off, so a pane closing
        // right behind its last turn would otherwise race the write and lose it. The trail swallows its own
        // failures, so this waits on a task that does not fault. Looped rather than a single await: a reading
        // already dequeued off the tail before cancellation can still be sitting as a queued
        // Dispatcher.UIThread.Post callback, and awaiting yields the UI thread to run it — which can replace
        // _pendingUsageWrite with a newer task after the one just captured here. Relies on DisposeCoreAsync
        // itself running on the UI thread, same as the Post callback it is racing — otherwise the check-and-break
        // below would be a torn read against that callback's own field writes.
        while (_pendingUsageWrite is { } pendingUsageWrite)
        {
            await pendingUsageWrite;
            if (ReferenceEquals(_pendingUsageWrite, pendingUsageWrite))
            {
                break;
            }
        }

        // Dropped here rather than left to the view, which is not told when a session closes: the panel is simply
        // removed from the collection and its container leaves the tree, so the view's own DataContext hook never
        // fires. A screenshot that lands after that would otherwise find a live delegate, paste into a terminal
        // that no longer exists, and report success with nothing to show for it (AC-226).
        PasteTextAsync = null;
    }
}
