using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// Keeps the pending resumes (AC-234) and sends each one when its moment arrives: the prompt a session picks up
// with after an allowance rolls over, or whenever the operator said to.
//
// One prompt per schedule, deliberately — no chaining, no conditions, no follow-up steps. That is Autopilot's
// job and it has its own approval flow; a resume that starts needing "and then" belongs there instead.
public sealed class ScheduledResumeCoordinator : ISingletonService, IDisposable
{
    // How far past its moment a resume may still fire. Covers the app being open and merely between ticks; beyond
    // it, the cockpit was closed and firing late would be a surprise rather than a service.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(30);

    private readonly IScheduledResumeStore _store;
    private readonly IToastService? _toast;
    private readonly ILogger<ScheduledResumeCoordinator> _logger;
    private readonly TimeSpan _tickInterval;
    private readonly List<ScheduledResume> _pending = [];
    private DispatcherTimer? _timer;
    private bool _started;
    private bool _disposed;

    // Resolves a pane id to the live session panel, or null when that pane is gone. Set by the cockpit, which owns the session list.
    public Func<string, SessionPanelViewModel?>? ResolveSession { get; set; }

    // Tries to reopen a pane that is gone, or merely restored and not yet started, and send the prompt into it —
    // AC-290. Returns whether that landed. Set by the cockpit, which alone knows how to reopen a session's earlier
    // conversation (AC-410's restore machinery); null when it cannot (unit tests, the designer), in which case a
    // resume whose pane cannot be sent into always falls straight through to the lapsed/undelivered report below,
    // exactly as it did before this existed. Deliberately silent and unattended — a resume was scheduled ahead of
    // time precisely to run while nobody is at the desk, so this never asks first.
    public Func<string, string, Task<bool>>? ReopenAndSend { get; set; }

    // Raised when the set of pending resumes changes, so a session can show or drop its "resuming at …" line.
    //
    // Raised on whichever thread called in, which in this app is always the UI thread: schedule and cancel come
    // from commands, firing comes from the timer, and the load comes from startup. That is why nothing in this
    // file uses `ConfigureAwait(false)` any more — it is what took the continuation off the UI thread, and a
    // handler that touches bound state has to stay on it (AC-368).
    public event EventHandler? PendingChanged;

    // `tickInterval`:
    // How often the clock is looked at. Overridden only by the tests, which cannot wait half a minute to watch a
    // timer that is supposed to tick — and watching it tick is the whole point after AC-368, where it never did.
    public ScheduledResumeCoordinator(
        IScheduledResumeStore store,
        IToastService? toast = null,
        ILogger<ScheduledResumeCoordinator>? logger = null,
        TimeSpan? tickInterval = null)
    {
        _store = store;
        _toast = toast;
        _logger = logger ?? NullLogger<ScheduledResumeCoordinator>.Instance;
        _tickInterval = tickInterval ?? DefaultTickInterval;
    }

    // Every resume still waiting, soonest first.
    public IReadOnlyList<ScheduledResume> Pending => _pending;

    // The resume waiting on `paneId`, or null when that session has none.
    public ScheduledResume? PendingFor(string paneId) =>
        _pending.FirstOrDefault(resume => resume.PaneId == paneId);

    // Loads what was scheduled before and reports whatever lapsed while the cockpit was closed, then starts
    // watching the clock. Idempotent: a second call is ignored rather than starting a second timer.
    //
    // Await it — never `.GetAwaiter().GetResult()` from the UI thread. It posts the timer's construction to
    // the dispatcher, so a caller that blocks the UI thread waiting for this is waiting for itself. That rules it
    // out as an `IHostedService`, whose signature it otherwise matches, because
    // `Program.StartHostedServices` starts those synchronously.
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Claimed before the first await, not after: the old guard read a field that is only set once the load is
        // done, so two starts that overlap both got past it. The second assignment then loses the first timer,
        // which goes on ticking with nothing holding it — Dispose cannot stop what it no longer has.
        if (_started)
        {
            return;
        }

        _started = true;

        try
        {
            await LoadAsync(cancellationToken);
        }
        catch
        {
            // Handed back so the claim does not outlive the attempt. A locked or unparseable config file is a
            // reason to try again, not a reason to be silently switched off for the rest of the run — which is the
            // very shape of failure this ticket exists to remove.
            _started = false;
            throw;
        }

        // Built on the UI thread deliberately (AC-368). Avalonia binds a DispatcherTimer to the dispatcher of the
        // thread that creates it, and the load above reads a file, so its continuation can land on a thread pool
        // thread — one with no message loop, where Start() throws nothing and Tick never fires. That is how every
        // scheduled resume since the feature shipped was silently never sent.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Disposed while the load was still running: the timer is built here regardless, so it has to be
            // checked here too, or shutdown leaves one ticking that Dispose already looked for and did not find.
            if (_disposed)
            {
                return;
            }

            _timer = new DispatcherTimer { Interval = _tickInterval };
            _timer.Tick += _OnTick;
            _timer.Start();
        });

        _logger.LogInformation(
            "Scheduled resumes are running, checking every {Interval} — {Count} waiting.",
            _tickInterval,
            _pending.Count);
    }

    // Takes up what was scheduled before this run and reports whatever lapsed while the cockpit was closed,
    // without starting the clock. Split from `StartAsync` so this half — which is where the judgement
    // lives — can be exercised without a timer, and a timer is never left running behind a test.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.LoadAsync(cancellationToken);
        var now = DateTimeOffset.Now;

        // Replaces rather than appends: this says what is waiting, and loading twice must not mean every resume is
        // held — and then delivered — twice. Filtered rather than Except'd for the same reason: set difference
        // would also quietly drop a duplicate that really is in the file.
        var lapsed = stored.Where(resume => resume.HasLapsed(now, Grace)).ToList();

        _pending.Clear();
        _pending.AddRange(stored.Where(resume => !lapsed.Contains(resume)));

        foreach (var missed in lapsed)
        {
            // Said out loud rather than fired late or dropped quietly: a resume that silently never happened is
            // worse than one that never existed, because it was counted on.
            _toast?.Show($"A resume set for {missed.DueAt.ToLocalTime():ddd HH:mm} did not run — the cockpit was closed.", ToastSeverity.Warning);
            _logger.LogWarning(
                "A resume for session {Pane}, due {DueAt:u}, lapsed while the cockpit was closed and was dropped.",
                missed.PaneId,
                missed.DueAt);
        }

        if (lapsed.Count > 0)
        {
            await _PersistAsync(cancellationToken);
        }

        _logger.LogInformation("Took up {Count} waiting resume(s) from the last run.", _pending.Count);

        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    // Schedules `resume`, replacing whatever that session had pending — one resume per session.
    public async Task ScheduleAsync(ScheduledResume resume, CancellationToken cancellationToken = default)
    {
        _pending.RemoveAll(existing => existing.PaneId == resume.PaneId);
        _pending.Add(resume);

        await _PersistAsync(cancellationToken);

        _logger.LogInformation(
            "Resume scheduled for session {Pane} at {DueAt:u} — {Reason}.",
            resume.PaneId,
            resume.DueAt,
            resume.Reason ?? "no reason given");

        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    // Cancels the resume waiting on `paneId`, removing it from storage rather than only from view.
    public async Task CancelAsync(string paneId, CancellationToken cancellationToken = default)
    {
        if (_pending.RemoveAll(resume => resume.PaneId == paneId) == 0)
        {
            return;
        }

        await _PersistAsync(cancellationToken);

        // Logged for the same reason as the rest: every route that empties the store leaves a line, so a resume
        // that turns out to be gone can be told apart from one that was never written down.
        _logger.LogInformation("Resume for session {Pane} cancelled.", paneId);

        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    // Sends every resume whose moment has come. Exposed for the tests, which drive the clock rather than waiting
    // half a minute for a timer tick.
    public async Task RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var due = _pending.Where(resume => resume.IsDue(now)).ToList();
        if (due.Count == 0)
        {
            return;
        }

        foreach (var resume in due)
        {
            _pending.Remove(resume);

            // AC-410: pane-id continuity means ResolveSession can now find a pane that was only just restored and
            // never started — its runtime is not there to send into. CanTakeAPrompt is the one place that already
            // answers "would a send actually reach the agent" (SessionPanelViewModel's own doc on the property), so
            // it is checked before SendPromptAsync is even called rather than trusted to fail safely on its own:
            // a session kind whose SendPromptAsync does not re-check its own readiness would otherwise report a
            // resume as delivered when it went nowhere.
            if (ResolveSession?.Invoke(resume.PaneId) is { CanTakeAPrompt: true } session && await session.SendPromptAsync(resume.Prompt))
            {
                _logger.LogInformation("Resume for session {Pane}, due {DueAt:u}, was sent.", resume.PaneId, resume.DueAt);
                continue;
            }

            if (await _TryReopenAsync(resume))
            {
                continue;
            }

            // The session is gone, not yet started, or could not take the prompt, and it could not be reopened
            // either. Never send it into a fresh one: "continue" with no history behind it is meaningless, and
            // worse than doing nothing because it looks like it worked.
            _toast?.Show("A resume could not be delivered — its session is no longer open.", ToastSeverity.Warning);
            _logger.LogWarning(
                "Resume for session {Pane}, due {DueAt:u}, could not be delivered — that session is gone, not started, or would not take it.",
                resume.PaneId,
                resume.DueAt);
        }

        await _PersistAsync(cancellationToken);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    // AC-290's other half: before reporting a resume as undelivered, gives `ReopenAndSend` a chance to
    // bring the session back and send into it. False for every reason that cannot happen — nothing set, nothing
    // to reopen with, the reopen itself failed or threw — which is deliberately indistinguishable to the caller:
    // each of those is the same honest fallback, not a new failure mode of its own.
    private async Task<bool> _TryReopenAsync(ScheduledResume resume)
    {
        if (ReopenAndSend is null)
        {
            return false;
        }

        bool delivered;
        try
        {
            delivered = await ReopenAndSend(resume.PaneId, resume.Prompt);
        }
        catch (Exception exception)
        {
            // Reopening reaches into session launch, which can fail in ways a scheduler tick must survive — same
            // discipline as _OnTick below: log it, never let it take the next tick down with it.
            _logger.LogWarning(exception, "Reopening session {Pane} for its resume threw; falling back to the undelivered report.", resume.PaneId);
            return false;
        }

        if (!delivered)
        {
            return false;
        }

        _toast?.Show($"A resume for {resume.DueAt.ToLocalTime():ddd HH:mm} reopened its session and was sent.", ToastSeverity.Success);
        _logger.LogInformation("Resume for session {Pane}, due {DueAt:u}, reopened its session and was sent.", resume.PaneId, resume.DueAt);
        return true;
    }

    private async void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            await RunDueAsync(DateTimeOffset.Now);
        }
        catch (Exception exception)
        {
            // A failed send or a config write caught mid-flight. The next tick tries again; a scheduler must never
            // be the reason the cockpit falls over — but it must leave a trace, or a resume that never arrives has
            // no explanation anywhere (AC-368).
            _logger.LogError(exception, "A scheduled-resume tick failed; the next one will try again.");
        }
    }

    private Task _PersistAsync(CancellationToken cancellationToken) =>
        _store.SaveAsync([.. _pending.OrderBy(resume => resume.DueAt)], cancellationToken);

    public void Dispose()
    {
        // Set before anything else, so a StartAsync still waiting on its load builds no timer when it comes back.
        // _started stays claimed: this is teardown, and a coordinator that has been disposed does not start again.
        _disposed = true;

        ResolveSession = null;
        ReopenAndSend = null;
        PendingChanged = null;

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
