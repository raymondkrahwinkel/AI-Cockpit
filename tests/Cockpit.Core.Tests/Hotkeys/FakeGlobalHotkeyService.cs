using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Core.Tests.Hotkeys;

/// <summary>Test double for <see cref="IGlobalHotkeyService"/>: lets a test raise the key events directly and records what was armed.</summary>
internal sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler<string>? Pressed;
    public event EventHandler<string>? Released;
    public event EventHandler? TriggerDescriptionsChanged;

    /// <summary>What a real one would report once armed, by hotkey id. Set entries to stand in for a compositor that bound something other than what was asked.</summary>
    public Dictionary<string, string> TriggerDescriptions { get; } = [];

    /// <summary>The bindings of the most recent <see cref="StartAsync"/> — what the operator's settings came out as.</summary>
    public IReadOnlyList<GlobalHotkeyBinding> LastBindings { get; private set; } = [];

    public bool WasStarted { get; private set; }

    /// <summary>How often it was armed — re-arming on a changed key is the difference between one and two.</summary>
    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    /// <summary>
    /// How many handlers are listening for a press. Counted rather than inferred from a raised event: the real
    /// handler marshals through a dispatcher no unit test pumps, so a double subscription is invisible from the
    /// far side — and a double subscription is exactly what re-arming must not leave behind.
    /// </summary>
    public int PressedSubscriberCount => Pressed?.GetInvocationList().Length ?? 0;

    /// <summary>Set to make arming the hook fail — the real ones can: a portal that refuses the shortcut, a hook the OS will not install.</summary>
    public Exception? StartFailure { get; init; }

    public string? TriggerDescriptionFor(string hotkeyId) => TriggerDescriptions.GetValueOrDefault(hotkeyId);

    public Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        StartCallCount++;
        if (StartFailure is not null)
        {
            return Task.FromException(StartFailure);
        }

        LastBindings = bindings;
        WasStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>Stands in for the operator rebinding a shortcut in their desktop's own settings.</summary>
    public void RaiseTriggerDescriptionChanged(string hotkeyId, string? description)
    {
        if (description is null)
        {
            TriggerDescriptions.Remove(hotkeyId);
        }
        else
        {
            TriggerDescriptions[hotkeyId] = description;
        }

        TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaisePressed(string hotkeyId) => Pressed?.Invoke(this, hotkeyId);

    public void RaiseReleased(string hotkeyId) => Released?.Invoke(this, hotkeyId);
}
