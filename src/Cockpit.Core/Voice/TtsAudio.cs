namespace Cockpit.Core.Voice;

/// <summary>
/// Synthesized speech as mono, normalized float32 PCM ([-1, 1]) plus the engine's own sample rate
/// (the rate the SupertonicTTS model emits) — <see cref="SampleRate"/> travels
/// with the samples so playback opens the device at the right rate instead of assuming a fixed one.
/// </summary>
public sealed record TtsAudio(float[] Samples, int SampleRate);
