using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Test double for <see cref="IGlobalHotkeyService"/>: lets a test raise the hold events directly and records whether the service was started.</summary>
internal sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;
    public event EventHandler? TriggerDescriptionChanged;

    /// <summary>What a real one would report once armed. Set it to stand in for a compositor that bound something other than what was asked.</summary>
    public string? TriggerDescription { get; set; }

    public bool WasStarted { get; private set; }

    /// <summary>How often it was armed — re-arming on a changed key is the difference between one and two.</summary>
    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    /// <summary>
    /// How many handlers are listening for a hold. Counted rather than inferred from a raised event: the real
    /// handler marshals through a dispatcher no unit test pumps, so a double subscription is invisible from the
    /// far side — and a double subscription is exactly what re-arming must not leave behind.
    /// </summary>
    public int HoldStartedSubscriberCount => HoldStarted?.GetInvocationList().Length ?? 0;

    /// <summary>Set to make arming the hook fail — the real ones can: a portal that refuses the shortcut, a hook the OS will not install.</summary>
    public Exception? StartFailure { get; init; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCallCount++;
        if (StartFailure is not null)
        {
            return Task.FromException(StartFailure);
        }

        WasStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>Stands in for the operator rebinding the shortcut in their desktop's own settings.</summary>
    public void RaiseTriggerDescriptionChanged(string? description)
    {
        TriggerDescription = description;
        TriggerDescriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseHoldStarted() => HoldStarted?.Invoke(this, EventArgs.Empty);

    public void RaiseHoldEnded() => HoldEnded?.Invoke(this, EventArgs.Empty);
}
