namespace Cockpit.Core.Startup;

public enum StartupStepState
{
    Pending,
    Running,
    Slow,
    Done,
    Skipped,
    Failed,
}

public sealed class StartupStep
{
    internal StartupStep(string label, Func<CancellationToken, Task> run, bool essential, bool isPlugin)
    {
        Label = label;
        Run = run;
        Essential = essential;
        IsPlugin = isPlugin;
    }

    public string Label { get; }

    public bool IsPlugin { get; }

    // Never given up on: the cockpit cannot open without it.
    public bool Essential { get; }

    public StartupStepState State { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public string? Message { get; internal set; }

    public Exception? Error { get; internal set; }

    internal Func<CancellationToken, Task> Run { get; }
}

// AC-1827: the host times every step; only an essential one is ever waited for past GiveUpAfter.
public sealed class StartupSequence(TimeProvider? clock = null, TimeSpan? slowAfter = null, TimeSpan? giveUpAfter = null)
{
    public TimeSpan SlowAfter { get; } = slowAfter ?? TimeSpan.FromSeconds(4);

    public TimeSpan GiveUpAfter { get; } = giveUpAfter ?? TimeSpan.FromSeconds(12);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<StartupStep> _steps = [];
    private readonly Lock _gate = new();

    // Raised from whichever thread moved a step; a subscriber marshals for itself.
    public event Action? Changed;

    public IReadOnlyList<StartupStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public DateTimeOffset Now => _clock.GetUtcNow();

    public StartupStep AddCore(string label, Func<CancellationToken, Task> run, bool essential = false) => _Add(label, run, essential, plugin: false);

    public StartupStep AddPlugin(string label, Func<CancellationToken, Task> run) => _Add(label, run, essential: false, plugin: true);

    private StartupStep _Add(string label, Func<CancellationToken, Task> run, bool essential, bool plugin)
    {
        var step = new StartupStep(label, run, essential, plugin);
        lock (_gate)
        {
            _steps.Add(step);
        }

        Changed?.Invoke();

        return step;
    }

    // Core steps run in order, plugin steps beside them; an essential failure is thrown once all the others have ended.
    public async Task RunPendingAsync()
    {
        StartupStep[] pending;
        lock (_gate)
        {
            pending = [.. _steps.Where(step => step.State == StartupStepState.Pending)];
        }

        async Task RunCoreAsync()
        {
            foreach (var step in pending.Where(step => !step.IsPlugin))
            {
                await RunAsync(step).ConfigureAwait(false);
                if (step is { Essential: true, State: StartupStepState.Failed })
                {
                    break;
                }
            }
        }

        await Task.WhenAll([RunCoreAsync(), .. pending.Where(step => step.IsPlugin).Select(RunAsync)]).ConfigureAwait(false);
        if (pending.FirstOrDefault(step => step is { Essential: true, State: StartupStepState.Failed }) is { Error: { } error })
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public Task RetryAsync(StartupStep step) => RunAsync(step);

    private async Task RunAsync(StartupStep step)
    {
        step.StartedAt = _clock.GetUtcNow();
        step.Message = null;
        step.Error = null;
        _Move(step, StartupStepState.Running);

        using var cancellation = new CancellationTokenSource();

        // Task.Run so a step that works before its first await cannot hold the caller either.
        var work = Task.Run(() => step.Run(cancellation.Token));
        try
        {
            if (!await _FinishesWithinAsync(work, SlowAfter).ConfigureAwait(false))
            {
                _Move(step, StartupStepState.Slow);
                if (!step.Essential && !await _FinishesWithinAsync(work, GiveUpAfter - SlowAfter).ConfigureAwait(false))
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                    _ = work.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                    step.Message = $"“{_Name(step)}” is taking too long — carrying on without it.";
                    _Move(step, StartupStepState.Skipped);

                    return;
                }
            }

            await work.ConfigureAwait(false);
            _Move(step, StartupStepState.Done);
        }
        catch (Exception exception)
        {
            step.Error = exception;
            step.Message = $"Couldn’t complete “{_Name(step)}” — carrying on without it.";
            _Move(step, StartupStepState.Failed);
        }
    }

    private async Task<bool> _FinishesWithinAsync(Task work, TimeSpan limit)
    {
        using var stop = new CancellationTokenSource();
        var timer = Task.Delay(limit, _clock, stop.Token);
        var first = await Task.WhenAny(work, timer).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);

        return first == work;
    }

    private static string _Name(StartupStep step) => step.Label.TrimEnd('…', '.', ' ');

    private void _Move(StartupStep step, StartupStepState state)
    {
        step.State = state;
        Changed?.Invoke();
    }
}
