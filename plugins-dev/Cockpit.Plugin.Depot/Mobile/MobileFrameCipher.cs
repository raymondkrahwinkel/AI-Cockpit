using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2222: one side of a stream's frames (PROTOCOL.md § 5): AES-256-GCM, a key and a nonce counter per direction. Only the
// very next counter opens, so a replay or a frame the relay left out is refused, as is a bad tag. A refusal is `false`,
// never an exception: nothing of the frame leaves this class.
public sealed class MobileFrameCipher : IDisposable
{
    public const byte Version = 1;

    private const int HeaderBytes = 1 + sizeof(ulong);
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    // The relay carries at most 65,536 bytes per frame (DEP-629), header and tag included.
    public const int MaxFrameBytes = 64 * 1024;
    public const int MaxPlaintextBytes = MaxFrameBytes - HeaderBytes - TagBytes;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly Lock _gate = new();
    private ulong _nextSend;
    private bool _sendExhausted;
    private ulong _nextReceive;
    private bool _receiveExhausted;

    public MobileFrameCipher(byte[] sendKey, byte[] receiveKey)
    {
        _send = new AesGcm(sendKey, TagBytes);
        _receive = new AesGcm(receiveKey, TagBytes);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintextBytes)
        {
            throw new ArgumentException($"A frame carries at most {MaxPlaintextBytes} bytes.", nameof(plaintext));
        }

        lock (_gate)
        {
            // A counter that wrapped would reuse a nonce under the same key, which breaks GCM outright.
            if (_sendExhausted)
            {
                throw new InvalidOperationException("This stream has sent all the frames it may; open a new stream.");
            }

            var frame = new byte[HeaderBytes + plaintext.Length + TagBytes];
            var header = frame.AsSpan(0, HeaderBytes);
            header[0] = Version;
            BinaryPrimitives.WriteUInt64BigEndian(header[1..], _nextSend);

            _send.Encrypt(_Nonce(_nextSend), plaintext, frame.AsSpan(HeaderBytes, plaintext.Length), frame.AsSpan(frame.Length - TagBytes), header);

            _sendExhausted = _nextSend == ulong.MaxValue;
            _nextSend++;
            return frame;
        }
    }

    public bool TryOpen(ReadOnlySpan<byte> frame, out byte[] plaintext)
    {
        plaintext = [];
        if (frame.Length < HeaderBytes + TagBytes || frame.Length > HeaderBytes + MaxPlaintextBytes + TagBytes || frame[0] != Version)
        {
            return false;
        }

        var header = frame[..HeaderBytes];
        var counter = BinaryPrimitives.ReadUInt64BigEndian(header[1..]);

        lock (_gate)
        {
            if (_receiveExhausted || counter != _nextReceive)
            {
                return false;
            }

            var body = new byte[frame.Length - HeaderBytes - TagBytes];
            try
            {
                _receive.Decrypt(_Nonce(counter), frame.Slice(HeaderBytes, body.Length), frame[^TagBytes..], body, header);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(body);
                return false;
            }

            // Only a frame that authenticated moves the counter: a forged one cannot skip the stream ahead or lock it.
            _receiveExhausted = counter == ulong.MaxValue;
            _nextReceive++;
            plaintext = body;
            return true;
        }
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }

    private static byte[] _Nonce(ulong counter)
    {
        var nonce = new byte[NonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(NonceBytes - sizeof(ulong)), counter);
        return nonce;
    }
}
