namespace Cockpit.Core.Voice;

/// <summary>
/// Synthesized speech as mono, normalized float32 PCM ([-1, 1]) plus the engine's own sample rate
/// (22050 Hz for most Piper voices, 16000 for some low-quality ones) — <see cref="SampleRate"/> travels
/// with the samples so playback opens the device at the right rate instead of assuming a fixed one.
/// </summary>
public sealed record TtsAudio(float[] Samples, int SampleRate);
