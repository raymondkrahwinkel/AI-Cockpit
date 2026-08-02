namespace Cockpit.Core.Audio;

// Describes a raw PCM audio stream. Defaults match the Whisper target format (16 kHz mono, s16le).
public sealed record AudioFormat(int SampleRate = 16000, int Channels = 1, int BitsPerSample = 16);
