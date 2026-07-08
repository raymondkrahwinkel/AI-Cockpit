namespace Cockpit.Core.Audio;

/// <summary>
/// Pure PCM sample-format conversion for TTS playback: the synthesis engine hands back normalized
/// float32 mono ([-1, 1]), <see cref="Abstractions.Audio.IAudioPlaybackService"/> plays signed 16-bit
/// little-endian bytes — the mirror image of <c>VoicePushToTalkService._ToFloatSamples</c> (int16 bytes
/// to float, for STT input).
/// </summary>
public static class PcmSampleConverter
{
    public static byte[] ToInt16Bytes(IReadOnlyList<float> samples)
    {
        var bytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
        {
            var s16 = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)(s16 & 0xFF);
            bytes[(i * 2) + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return bytes;
    }
}
