using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Startup;

namespace Cockpit.App.ViewModels;

public sealed class SplashSegment(StartupStepState state)
{
    public bool IsDone { get; } = state == StartupStepState.Done;

    public bool IsActive { get; } = state is StartupStepState.Running or StartupStepState.Slow;

    public bool IsFailed { get; } = state == StartupStepState.Failed;

    public bool IsSkipped { get; } = state == StartupStepState.Skipped;
}

// AC-1827: real steps and no percentage, which would lie exactly when a step hangs; refreshed on a tick as well as on change.
public sealed partial class SplashViewModel : ObservableObject
{
    // So a start that finishes at once never flashes the step UI.
    public static readonly TimeSpan StepsAppearAfter = TimeSpan.FromMilliseconds(400);

    // So the logo entrance finishes and a warm start does not flash.
    public static readonly TimeSpan MinimumShown = TimeSpan.FromMilliseconds(600);

    private readonly StartupSequence _sequence;
    private readonly DateTimeOffset _shownAt;
    private bool _unlocked;

    [ObservableProperty]
    private UnlockViewModel? _unlock;

    public bool IsUnlocking => Unlock is not null;

    public bool HasUnlockStep => IsUnlocking || _unlocked;

    public bool HasUnlockError => !string.IsNullOrEmpty(Unlock?.Error);

    public void BeginUnlock(UnlockViewModel unlock)
    {
        Unlock = unlock;
        unlock.PropertyChanged += _UnlockChanged;
        OnPropertyChanged(nameof(IsUnlocking));
        OnPropertyChanged(nameof(HasUnlockStep));
        Refresh();
    }

    public void EndUnlock()
    {
        if (Unlock is { } unlock)
        {
            unlock.PropertyChanged -= _UnlockChanged;
            unlock.Password = string.Empty;
        }

        Unlock = null;
        _unlocked = true;
        OnPropertyChanged(nameof(IsUnlocking));
        OnPropertyChanged(nameof(HasUnlockStep));
        Refresh();
    }

    private void _UnlockChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUnlockError));
        Refresh();
    }

    public SplashViewModel(StartupSequence sequence)
    {
        _sequence = sequence;
        _shownAt = sequence.Now;
        Version = Cockpit.Core.Plugins.HostVersionInfo.Current.ToString(3);
        Refresh();
    }

    public string Version { get; }

    public ObservableCollection<SplashSegment> Segments { get; } = [];

    [ObservableProperty]
    private bool _stepsVisible;

    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private string _position = "";

    [ObservableProperty]
    private bool _isSlow;

    [ObservableProperty]
    private bool _hasFailure;

    public TimeSpan Shown => _sequence.Now - _shownAt;

    public TimeSpan StillToHold => MinimumShown - Shown is { Ticks: > 0 } left ? left : TimeSpan.Zero;

    public void Refresh()
    {
        var steps = _sequence.Steps;
        var now = _sequence.Now;
        StepsVisible = IsUnlocking || _unlocked || Shown >= StepsAppearAfter;

        Segments.Clear();
        foreach (var step in steps)
        {
            Segments.Add(new SplashSegment(step.State));
        }

        var failed = steps.FirstOrDefault(step => step.State == StartupStepState.Failed);
        var active = steps.Where(step => step.State is StartupStepState.Running or StartupStepState.Slow)
            .OrderByDescending(step => step.State == StartupStepState.Slow)
            .FirstOrDefault();
        HasFailure = failed is not null && active is null;
        IsSlow = active is { State: StartupStepState.Slow };

        var done = steps.Count(step => step.State is StartupStepState.Done or StartupStepState.Skipped or StartupStepState.Failed);
        Position = steps.Count == 0 ? "" : $"Step {Math.Min(done + 1, steps.Count)} of {steps.Count}";

        if (Unlock is { } unlock)
        {
            Headline = unlock.IsBusy ? "Unlocking…" : "Unlock to continue";
            Detail = unlock.IsBusy ? "Checking the password" : "Your API keys and tokens are encrypted. Type the password to unlock them.";
            return;
        }

        if (active is not null)
        {
            var waited = active.StartedAt is { } started ? now - started : TimeSpan.Zero;
            Headline = IsSlow ? $"{active.Label} (slower than usual)" : active.Label;
            Detail = IsSlow ? _SlowDetail(_sequence, active, waited) : "";

            return;
        }

        Headline = failed?.Message ?? steps.LastOrDefault(step => step.State == StartupStepState.Skipped)?.Message ?? (_unlocked ? "Opening your cockpit…" : "Almost there…");
        Detail = "";
    }

    private static string _SlowDetail(StartupSequence sequence, StartupStep step, TimeSpan waited)
    {
        var seconds = (int)waited.TotalSeconds;
        if (step.Essential)
        {
            return $"Waited {seconds} s · Cockpit needs this step to start";
        }

        var left = Math.Max(0, (int)Math.Ceiling((sequence.GiveUpAfter - waited).TotalSeconds));

        return $"Waited {seconds} s · Cockpit carries on without it in {left} s";
    }
}
