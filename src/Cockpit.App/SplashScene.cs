using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Startup;

namespace Cockpit.App;

// AC-1827: a real StartupSequence on a hand-moved clock, so the slow, skipped and failed states are the host's own.
internal static class SplashScene
{
    private const string PluginLabel = "Connecting to the example service…";

    public static Window Fast() => _Build(_ => { });

    public static Window Loading() => _Build(scene =>
    {
        scene.Finish("Loading settings…", "Loading plugins…");
        scene.Clock.Advance(TimeSpan.FromSeconds(1.2));
    });

    public static Window Slow() => _Build(scene =>
    {
        scene.Finish("Loading settings…", "Loading plugins…", "Loading projects…", "Restoring sessions…");
        scene.Clock.Advance(TimeSpan.FromSeconds(4.5));
        scene.WaitFor(PluginLabel, StartupStepState.Slow);
        scene.WaitForTimer();
        scene.Clock.Advance(TimeSpan.FromSeconds(1.5));
    });

    public static Window Skipped() => _Build(scene =>
    {
        scene.Finish("Loading settings…", "Loading plugins…", "Loading projects…", "Restoring sessions…");
        scene.Clock.Advance(TimeSpan.FromSeconds(4.5));
        scene.WaitFor(PluginLabel, StartupStepState.Slow);
        scene.WaitForTimer();
        scene.Clock.Advance(TimeSpan.FromSeconds(8));
        scene.WaitFor(PluginLabel, StartupStepState.Skipped);
    });

    public static Window Failed() => _Build(scene =>
    {
        scene.Finish("Loading settings…", "Loading plugins…", "Loading projects…", "Restoring sessions…");
        scene.Clock.Advance(TimeSpan.FromSeconds(1));
        scene.Fail(PluginLabel);
        scene.WaitFor(PluginLabel, StartupStepState.Failed);
    });

    private static SplashWindow _Build(Action<Scene> arrange)
    {
        var scene = new Scene();
        var viewModel = new SplashViewModel(scene.Sequence);
        arrange(scene);
        viewModel.Refresh();

        return new SplashWindow(viewModel, null);
    }

    private sealed class Scene
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];

        public Scene()
        {
            Sequence = new StartupSequence(Clock);
            foreach (var label in new[] { "Loading settings…", "Loading plugins…", "Loading projects…", "Restoring sessions…" })
            {
                Sequence.AddCore(label, _ => _Gate(label).Task);
            }

            Sequence.AddPlugin(PluginLabel, _ => _Gate(PluginLabel).Task);
            _ = Sequence.RunPendingAsync();
            WaitFor("Loading settings…", StartupStepState.Running);
            WaitFor(PluginLabel, StartupStepState.Running);
        }

        public SceneClock Clock { get; } = new();

        public StartupSequence Sequence { get; }

        public void Finish(params string[] labels)
        {
            foreach (var label in labels)
            {
                _Gate(label).TrySetResult();
                WaitFor(label, StartupStepState.Done);
            }
        }

        // The give-up timer is created only after the Slow state shows, so advancing before it exists would miss it.
        public void WaitForTimer() => SpinWait.SpinUntil(() => Clock.Pending > 0, TimeSpan.FromSeconds(5));

        public void Fail(string label) => _Gate(label).TrySetException(new InvalidOperationException("The service did not answer."));

        public void WaitFor(string label, StartupStepState state)
        {
            var step = Sequence.Steps.First(candidate => candidate.Label == label);
            if (!SpinWait.SpinUntil(() => step.State == state, TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"Splash scene: \"{label}\" never reached {state}.");
            }
        }

        private TaskCompletionSource _Gate(string label)
        {
            lock (_gates)
            {
                return _gates.TryGetValue(label, out var gate) ? gate : _gates[label] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private sealed class SceneClock : TimeProvider
    {
        private readonly List<SceneTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public int Pending
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count(timer => timer.DueAt != DateTimeOffset.MaxValue);
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SceneTimer(callback, state, _now + dueTime);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            SceneTimer[] due;
            lock (_timers)
            {
                due = [.. _timers.Where(timer => timer.DueAt <= _now)];
                _timers.RemoveAll(timer => timer.DueAt <= _now);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }
    }

    private sealed class SceneTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; } = dueAt;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = DateTimeOffset.MaxValue;

            return true;
        }

        public void Dispose() => DueAt = DateTimeOffset.MaxValue;

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
