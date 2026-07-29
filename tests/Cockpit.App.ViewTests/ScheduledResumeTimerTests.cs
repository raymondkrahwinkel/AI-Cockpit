using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The half of a scheduled resume the old tests stepped over: the timer (AC-368). They called
/// <c>RunDueAsync</c> by hand, which is the part that always worked — while the clock behind it never ticked once
/// in the app, so no resume was ever sent since the feature shipped. The timer was built after an <c>await</c> that
/// had left the UI thread, and Avalonia binds a DispatcherTimer to whichever dispatcher its creator was on: a
/// thread pool thread has none, <c>Start()</c> throws nothing, and <c>Tick</c> simply never fires.
/// <para>
/// So these run against a real dispatcher, with a store that completes asynchronously the way the file-backed one
/// does — that asynchrony is precisely what threw the timer off the UI thread, and a store that returns a finished
/// task hides the bug by continuing inline.
/// </para>
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert: the fluent library its neighbours use is on its way out (AC-372).
/// </remarks>
[Collection("avalonia")]
public class ScheduledResumeTimerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LongEnoughToHaveTicked = TimeSpan.FromSeconds(5);

    /// <summary>A store that hands its answer back on another thread, as reading a file does.</summary>
    private sealed class YieldingStore : IScheduledResumeStore
    {
        private List<ScheduledResume> _saved;

        public YieldingStore(params ScheduledResume[] stored) => _saved = [.. stored];

        public Func<Task>? OnSave { get; set; }

        public Func<Task>? OnLoad { get; set; }

        public async Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            if (OnLoad is { } fail)
            {
                await fail();
            }

            return _saved;
        }

        public async Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            if (OnSave is { } fail)
            {
                await fail();
            }

            _saved = [.. resumes];
        }
    }

    private sealed class RecordingSession : SessionPanelViewModel
    {
        private readonly TaskCompletionSource<string> _first = new();

        public Task<string> FirstPrompt => _first.Task;

        // A live, ready session for these timer tests (AC-410's CanTakeAPrompt gate in RunDueAsync would otherwise
        // refuse to send into this fake the same way it now refuses an unstarted restored pane).
        public override bool CanTakeAPrompt => true;

        public override Task<bool> SendPromptAsync(string prompt)
        {
            _first.TrySetResult(prompt);
            return Task.FromResult(true);
        }

        protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

        protected override void OnVoiceTextReady(string text)
        {
        }

        public override Task<bool> FeedVerifyResultAsync(string text, byte[] image) => Task.FromResult(false);

        protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng) => Task.FromResult<string?>(null);
    }

    [Fact]
    public Task AResumeThatIsDue_IsSentByTheTimer_WithoutAnybodyDrivingIt() => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new RecordingSession();
        var due = new ScheduledResume(session.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: "Week is 95% used");

        using var coordinator = new ScheduledResumeCoordinator(new YieldingStore(due), toast: null, logger: null, Tick)
        {
            ResolveSession = _ => session,
        };

        await coordinator.StartAsync();

        Task arrived = await Task.WhenAny(session.FirstPrompt, Task.Delay(LongEnoughToHaveTicked));

        Assert.True(
            ReferenceEquals(arrived, session.FirstPrompt),
            "the timer never ticked, so the resume was never sent — which is the whole of AC-368");
        Assert.Equal("carry on", await session.FirstPrompt);
        Assert.Empty(coordinator.Pending);
    });

    [Fact]
    public Task ATickThatThrows_IsSurvived_AndTheNextOneStillFires() => HeadlessAvalonia.RunAsync(async () =>
    {
        // A scheduler must never be the reason the cockpit falls over — but it must not fail in silence either,
        // which is how this went unnoticed for as long as it did.
        var session = new RecordingSession();
        var due = new ScheduledResume(session.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: "Week is 95% used");

        var store = new YieldingStore(due) { OnSave = () => throw new IOException("the config file was locked") };
        var logger = new CountingLogger();

        using var coordinator = new ScheduledResumeCoordinator(store, toast: null, logger, Tick)
        {
            ResolveSession = _ => session,
        };

        await coordinator.StartAsync();

        Task arrived = await Task.WhenAny(session.FirstPrompt, Task.Delay(LongEnoughToHaveTicked));
        Assert.True(ReferenceEquals(arrived, session.FirstPrompt), "the prompt was sent before the write that failed");

        Task logged = await Task.WhenAny(logger.FirstError, Task.Delay(LongEnoughToHaveTicked));
        Assert.True(ReferenceEquals(logged, logger.FirstError), "the tick swallowed its exception without a word");
        Assert.IsType<IOException>(await logger.FirstError);
    });

    [Fact]
    public Task StartedFromAThreadPoolThread_TheTimerStillTicks() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The one that pins the fix itself rather than the house rule beside it. Without ConfigureAwait(false) the
        // continuation finds its way back to the UI thread on its own, so dropping the marshalling would go
        // unnoticed — until something starts the scheduler from a thread that never had a dispatcher to return to.
        var session = new RecordingSession();
        var due = new ScheduledResume(session.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null);

        using var coordinator = new ScheduledResumeCoordinator(new YieldingStore(due), toast: null, logger: null, Tick)
        {
            ResolveSession = _ => session,
        };

        await Task.Run(() => coordinator.StartAsync());

        Assert.Equal("carry on", await session.FirstPrompt.WaitAsync(LongEnoughToHaveTicked));
    });

    [Fact]
    public Task AStartThatFailed_CanBeStartedAgain() => HeadlessAvalonia.RunAsync(async () =>
    {
        // A config file held open for a moment, or one that does not parse, makes the load throw. If the claim on
        // "already started" survives that, the scheduler is off for the rest of the run and says it is running —
        // which is the exact shape of failure this ticket exists to remove.
        var session = new RecordingSession();
        var due = new ScheduledResume(session.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", Reason: null);

        var store = new YieldingStore(due) { OnLoad = () => throw new IOException("the config file was locked") };

        using var coordinator = new ScheduledResumeCoordinator(store, toast: null, logger: null, Tick)
        {
            ResolveSession = _ => session,
        };

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync());

        store.OnLoad = null;
        await coordinator.StartAsync();

        Assert.Equal("carry on", await session.FirstPrompt.WaitAsync(LongEnoughToHaveTicked));
    });

    [Fact]
    public Task TwoStartsThatOverlap_OnlyOneOfThemStarts() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The old guard looked at a field that is only set after the load, so two starts that overlap both got past
        // it. The loser's timer is then unreachable — Dispose cannot stop what it no longer holds — and keeps
        // ticking for the rest of the run.
        var logger = new CountingLogger();

        using var coordinator = new ScheduledResumeCoordinator(new YieldingStore(), toast: null, logger, Tick);

        await Task.WhenAll(coordinator.StartAsync(), coordinator.StartAsync());

        Assert.Equal(1, logger.Started);
    });

    /// <summary>
    /// Counts how often the coordinator announced that it is running — one line per timer that started — and hands
    /// back the first exception it logged, which is how a tick that failed is caught in the act rather than by
    /// waiting a fixed length of time and hoping.
    /// </summary>
    private sealed class CountingLogger : ILogger<ScheduledResumeCoordinator>
    {
        private readonly TaskCompletionSource<Exception> _firstError = new();

        public int Started { get; private set; }

        public Task<Exception> FirstError => _firstError.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Scheduled resumes are running"))
            {
                Started++;
            }

            if (exception is not null)
            {
                _firstError.TrySetResult(exception);
            }
        }
    }
}
