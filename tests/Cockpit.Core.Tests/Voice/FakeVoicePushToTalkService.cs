using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Test double for <see cref="IVoicePushToTalkService"/> that counts what is listening to its level feed.</summary>
/// <remarks>
/// A counted fake rather than a substitute, for the reason <see cref="FakeGlobalHotkeyService"/> is one: the
/// coordinator's level handler marshals through a dispatcher no unit test pumps, so a second subscription is
/// invisible from the far side — counting is the only way to see it, and it is the whole thing being guarded.
/// </remarks>
internal sealed class FakeVoicePushToTalkService : IVoicePushToTalkService
{
    public event EventHandler<double>? AudioLevelSampled;
    public event EventHandler<VoicePreparationProgress>? Preparing;
    public event EventHandler? Prepared;

    /// <summary>How many handlers are on the level feed. One per hold is the contract; more means they are stacking.</summary>
    public int AudioLevelSubscriberCount => AudioLevelSampled?.GetInvocationList().Length ?? 0;

    /// <summary>What <see cref="BeginHold"/> reports — false stands in for a hold the guard declined because one is already running.</summary>
    public bool HoldStarts { get; init; } = true;

    public bool BeginHold() => HoldStarts;

    public Task<string> EndHoldAsync(bool applyCleanup, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public void RaisePreparing(VoicePreparationProgress step) => Preparing?.Invoke(this, step);

    public void RaisePrepared() => Prepared?.Invoke(this, EventArgs.Empty);

    public void RaiseAudioLevelSampled(double level) => AudioLevelSampled?.Invoke(this, level);
}
