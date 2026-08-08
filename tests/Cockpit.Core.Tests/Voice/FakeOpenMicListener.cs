using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Test double for <see cref="IOpenMicListener"/>: records whether the capture loop was started and how many handlers are on its events.</summary>
/// <remarks>
/// A counted fake rather than a substitute, for the reason <see cref="FakeGlobalHotkeyService"/> is one: the real
/// handlers marshal through a dispatcher no unit test pumps, so what is subscribed cannot be read from the far
/// side by raising an event — and what stays subscribed to a listener that never started is the thing to assert.
/// </remarks>
internal sealed class FakeOpenMicListener : IOpenMicListener
{
    public event EventHandler<string>? UtteranceTranscribed;
    public event EventHandler? SpeechStarted;
    public event EventHandler? SpeechEnded;
    public event EventHandler<double>? AudioLevelSampled;

    public bool WasStarted { get; private set; }

    /// <summary>How many starts got through. More than one per enable is the AC-628 race: one microphone per start, and only the last one stoppable.</summary>
    public int StartCount { get; private set; }

    /// <summary>Set to make starting the capture loop fail — a real one can: a microphone another application holds, a device that went away.</summary>
    public Exception? StartFailure { get; init; }

    /// <summary>Set to hold every start open until the test completes it, so one caller can be parked inside <see cref="StartAsync"/> while a second races it (AC-628).</summary>
    public TaskCompletionSource? HoldStart { get; init; }

    /// <summary>How many handlers are listening for an utterance. The coordinator wires all four events together, so one count speaks for the set.</summary>
    public int UtteranceSubscriberCount => UtteranceTranscribed?.GetInvocationList().Length ?? 0;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (StartFailure is not null)
        {
            return Task.FromException(StartFailure);
        }

        WasStarted = true;
        StartCount++;
        return HoldStart?.Task ?? Task.CompletedTask;
    }

    public Task StopAsync()
    {
        WasStarted = false;
        return Task.CompletedTask;
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void RaiseUtteranceTranscribed(string text) => UtteranceTranscribed?.Invoke(this, text);

    public void RaiseSpeechStarted() => SpeechStarted?.Invoke(this, EventArgs.Empty);

    public void RaiseSpeechEnded() => SpeechEnded?.Invoke(this, EventArgs.Empty);

    public void RaiseAudioLevelSampled(double level) => AudioLevelSampled?.Invoke(this, level);
}
