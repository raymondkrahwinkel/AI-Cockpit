using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Keeps the pending resumes (AC-234) and sends each one when its moment arrives: the prompt a session picks up
/// with after an allowance rolls over, or whenever the operator said to.
/// <para>
/// One prompt per schedule, deliberately — no chaining, no conditions, no follow-up steps. That is Autopilot's
/// job and it has its own approval flow; a resume that starts needing "and then" belongs there instead.
/// </para>
/// </summary>
public sealed class ScheduledResumeCoordinator : ISingletonService, IDisposable
{
    // How far past its moment a resume may still fire. Covers the app being open and merely between ticks; beyond
    // it, the cockpit was closed and firing late would be a surprise rather than a service.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IScheduledResumeStore _store;
    private readonly IToastService? _toast;
    private readonly List<ScheduledResume> _pending = [];
    private DispatcherTimer? _timer;

    /// <summary>Resolves a pane id to the live session panel, or null when that pane is gone. Set by the cockpit, which owns the session list.</summary>
    public Func<string, SessionPanelViewModel?>? ResolveSession { get; set; }

    /// <summary>Raised when the set of pending resumes changes, so a session can show or drop its "resuming at …" line.</summary>
    public event EventHandler? PendingChanged;

    public ScheduledResumeCoordinator(IScheduledResumeStore store, IToastService? toast = null)
    {
        _store = store;
        _toast = toast;
    }

    /// <summary>Every resume still waiting, soonest first.</summary>
    public IReadOnlyList<ScheduledResume> Pending => _pending;

    /// <summary>The resume waiting on <paramref name="paneId"/>, or null when that session has none.</summary>
    public ScheduledResume? PendingFor(string paneId) =>
        _pending.FirstOrDefault(resume => resume.PaneId == paneId);

    /// <summary>
    /// Loads what was scheduled before and reports whatever lapsed while the cockpit was closed, then starts
    /// watching the clock. Idempotent: a second call is ignored rather than starting a second timer.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_timer is not null)
        {
            return;
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += _OnTick;
        _timer.Start();
    }

    /// <summary>
    /// Takes up what was scheduled before this run and reports whatever lapsed while the cockpit was closed,
    /// without starting the clock. Split from <see cref="StartAsync"/> so this half — which is where the judgement
    /// lives — can be exercised without a timer, and a timer is never left running behind a test.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;

        var lapsed = stored.Where(resume => resume.HasLapsed(now, Grace)).ToList();
        _pending.AddRange(stored.Except(lapsed));

        foreach (var missed in lapsed)
        {
            // Said out loud rather than fired late or dropped quietly: a resume that silently never happened is
            // worse than one that never existed, because it was counted on.
            _toast?.Show($"A resume set for {missed.DueAt.ToLocalTime():ddd HH:mm} did not run — the cockpit was closed.", ToastSeverity.Warning);
        }

        if (lapsed.Count > 0)
        {
            await _PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Schedules <paramref name="resume"/>, replacing whatever that session had pending — one resume per session.</summary>
    public async Task ScheduleAsync(ScheduledResume resume, CancellationToken cancellationToken = default)
    {
        _pending.RemoveAll(existing => existing.PaneId == resume.PaneId);
        _pending.Add(resume);

        await _PersistAsync(cancellationToken).ConfigureAwait(false);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels the resume waiting on <paramref name="paneId"/>, removing it from storage rather than only from view.</summary>
    public async Task CancelAsync(string paneId, CancellationToken cancellationToken = default)
    {
        if (_pending.RemoveAll(resume => resume.PaneId == paneId) == 0)
        {
            return;
        }

        await _PersistAsync(cancellationToken).ConfigureAwait(false);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sends every resume whose moment has come. Exposed for the tests, which drive the clock rather than waiting
    /// half a minute for a timer tick.
    /// </summary>
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

            if (ResolveSession?.Invoke(resume.PaneId) is { } session && await session.SendPromptAsync(resume.Prompt).ConfigureAwait(false))
            {
                continue;
            }

            // The session is gone, or could not take the prompt. Never send it into a fresh one: "continue" with
            // no history behind it is meaningless, and worse than doing nothing because it looks like it worked.
            _toast?.Show("A resume could not be delivered — its session is no longer open.", ToastSeverity.Warning);
        }

        await _PersistAsync(cancellationToken).ConfigureAwait(false);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            await RunDueAsync(DateTimeOffset.Now).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A failed send or a config write caught mid-flight. The next tick tries again; a scheduler must never
            // be the reason the cockpit falls over.
        }
    }

    private Task _PersistAsync(CancellationToken cancellationToken) =>
        _store.SaveAsync([.. _pending.OrderBy(resume => resume.DueAt)], cancellationToken);

    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
