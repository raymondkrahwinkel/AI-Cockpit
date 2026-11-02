using System.Buffers.Binary;
using System.Text;
using Cockpit.Core.Audio;

namespace Cockpit.App.Services;

internal static class PcmWaveFile
{
    private const int MaximumBytes = 10 * 1024 * 1024;

    public static (byte[] Pcm, AudioFormat Format) Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("The WAV file is too large or cannot be read directly.");
        }

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (_ReadFourCc(reader) != "RIFF" || reader.ReadUInt32() > MaximumBytes || _ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("The selected file is not a WAV file.");
        }

        AudioFormat? format = null;
        byte[]? pcm = null;
        while (reader.BaseStream.Position <= reader.BaseStream.Length - 8)
        {
            var chunk = _ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            if (chunkSize > MaximumBytes || reader.BaseStream.Position + chunkSize > reader.BaseStream.Length)
            {
                throw new InvalidDataException("The WAV file contains an invalid chunk.");
            }

            if (chunk == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("The WAV format block is incomplete.");
                }

                var encoding = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadInt32();
                reader.BaseStream.Seek(6, SeekOrigin.Current);
                var bitsPerSample = reader.ReadUInt16();
                reader.BaseStream.Seek(chunkSize - 16, SeekOrigin.Current);

                if (encoding != 1 || bitsPerSample != 16 || channels is < 1 or > 2 || sampleRate <= 0)
                {
                    throw new InvalidDataException("Only 16-bit PCM WAV files with one or two channels are supported.");
                }

                format = new AudioFormat(sampleRate, channels, bitsPerSample);
            }
            else if (chunk == "data")
            {
                pcm = reader.ReadBytes((int)chunkSize);
                if (pcm.Length != chunkSize)
                {
                    throw new InvalidDataException("The WAV audio data is incomplete.");
                }
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }

        return format is not null && pcm is not null
            ? (pcm, format)
            : throw new InvalidDataException("The WAV file has no PCM format or audio data.");
    }

    public static void ApplyVolume(Span<byte> pcm, double volume)
    {
        var scale = Math.Clamp(volume, 0, 1);
        for (var offset = 0; offset + 1 < pcm.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[offset..], (short)(sample * scale));
        }
    }

    private static string _ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new InvalidDataException("The WAV file ended unexpectedly.");
        }

        return Encoding.ASCII.GetString(bytes);
    }
}
