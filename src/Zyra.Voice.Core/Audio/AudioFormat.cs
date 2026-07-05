namespace Zyra.Voice.Core.Audio;

/// <summary>
/// Describes a raw PCM audio stream. Defaults match the Whisper target format (16 kHz mono, s16le).
/// </summary>
public sealed record AudioFormat(int SampleRate = 16000, int Channels = 1, int BitsPerSample = 16);
