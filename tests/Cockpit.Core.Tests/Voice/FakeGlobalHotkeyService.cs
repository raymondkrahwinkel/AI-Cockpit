using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Test double for <see cref="IGlobalHotkeyService"/>: lets a test raise the hold events directly and records whether the service was started.</summary>
internal sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;

    public bool WasStarted { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        WasStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void RaiseHoldStarted() => HoldStarted?.Invoke(this, EventArgs.Empty);

    public void RaiseHoldEnded() => HoldEnded?.Invoke(this, EventArgs.Empty);
}
