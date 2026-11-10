using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2265: a paired phone's images, in chunks, until a send names them (PROTOCOL.md § 12). Limits are checked on the
// bytes as they come in, never on what the phone declares; a refusal drops the attachment and is the last word on its id.
internal sealed class MobileAttachments : IDisposable
{
    public const int MaxChunkBytes = 48_000;
    public const int MaxOpen = 10;
    public const long MaxBufferedBytes = 20_000_000;
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);

    private const int _MaxIdLength = 64;
    private const int _MaxChunks = (AssistantChannelImageLimits.MaxBytes + MaxChunkBytes - 1) / MaxChunkBytes;

    // ponytail: refused ids are forgotten wholesale past this many in one stream, after which a late chunk of an old
    // refused attachment is refused again rather than ignored; only a phone ignoring § 12 ever gets there.
    private const int _MaxRememberedRefusals = 64;

    private static readonly string[] _Mimes = ["image/jpeg", "image/png", "image/webp"];

    private readonly Action<string, string> _refused;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedIds = new(StringComparer.Ordinal);
    private ITimer? _sweep;
    private long _buffered;
    private bool _disposed;

    // `refused(attachmentId, reason)` is called outside the lock, once per refused attachment.
    public MobileAttachments(Action<string, string> refused, TimeProvider? time = null)
    {
        _refused = refused;
        _time = time ?? TimeProvider.System;
    }

    public void Add(string? attachmentId, int? seq, int? total, string? mime, string? data)
    {
        string? refusal;
        List<string> expired;
        lock (_gate)
        {
            if (_disposed || attachmentId is null || _refusedIds.Contains(attachmentId))
            {
                return;
            }

            expired = _Sweep();
            refusal = _Add(attachmentId, seq, total, mime, data);
            if (refusal is not null)
            {
                _Drop(attachmentId);
            }
        }

        _ReportExpired(expired);
        if (refusal is not null)
        {
            _refused(attachmentId, refusal);
        }
    }

    // The images under these ids, at most AssistantChannelImageLimits.MaxPerMessage; every named id is used up, complete
    // or not, and the refusal says what did not come along.
    public (IReadOnlyList<byte[]> Images, string? Refusal) Take(IReadOnlyList<string> attachmentIds)
    {
        if (attachmentIds.Count == 0)
        {
            return ([], null);
        }

        List<byte[]> images = [];
        var missing = false;
        List<string> expired;
        lock (_gate)
        {
            expired = _Sweep();
            foreach (var id in attachmentIds.Distinct(StringComparer.Ordinal))
            {
                if (_pending.Remove(id, out var pending))
                {
                    _buffered -= pending.Bytes;
                    if (pending.Complete && images.Count < AssistantChannelImageLimits.MaxPerMessage)
                    {
                        images.Add(pending.Join());
                        continue;
                    }
                }

                missing |= images.Count < AssistantChannelImageLimits.MaxPerMessage;
            }

            _StopSweepWhenIdle();
        }

        _ReportExpired(expired);
        List<string> refusals = [];
        if (missing)
        {
            refusals.Add("an attachment was missing, incomplete or refused");
        }

        if (attachmentIds.Count > AssistantChannelImageLimits.MaxPerMessage)
        {
            refusals.Add($"only the first {AssistantChannelImageLimits.MaxPerMessage} images of a message are passed on");
        }

        return (images, refusals.Count == 0 ? null : string.Join("; ", refusals));
    }

    // The stream is gone: nothing of it is finished any more, and a new stream starts with a clean slate.
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _refusedIds.Clear();
            _buffered = 0;
            _StopSweepWhenIdle();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Clear();
    }

    // Under _gate. Null when the chunk was taken.
    private string? _Add(string id, int? seq, int? total, string? mime, string? data)
    {
        if (id.Length is 0 or > _MaxIdLength)
        {
            return "the attachment id is empty or too long";
        }

        if (mime is null || !_Mimes.Contains(mime, StringComparer.Ordinal))
        {
            return "only JPEG, PNG and WebP images can be sent";
        }

        if (total is not > 0 || total > _MaxChunks)
        {
            return $"the image is bigger than {AssistantChannelImageLimits.MaxBytes / (1024 * 1024)} MB";
        }

        if (!_pending.TryGetValue(id, out var pending))
        {
            if (seq != 0)
            {
                return "a chunk arrived out of order";
            }

            if (_pending.Count >= MaxOpen)
            {
                return $"at most {MaxOpen} images can be on their way at once";
            }

            pending = new Pending(mime, total.Value);
        }
        else if (seq != pending.Chunks.Count || mime != pending.Mime || total != pending.Total || pending.Complete)
        {
            return "a chunk arrived out of order";
        }

        // A string longer than this cannot decode to a chunk that fits, so it is refused before it is decoded.
        if (data is null || data.Length > (MaxChunkBytes + 2) / 3 * 4)
        {
            return "a chunk is bigger than allowed";
        }

        var chunk = new byte[MaxChunkBytes];
        if (!Convert.TryFromBase64String(data, chunk, out var length) || length == 0)
        {
            return "a chunk is damaged";
        }

        // The format's signature is in the first chunk, so a mislabelled file goes before it takes any memory.
        if (pending.Chunks.Count == 0 && !_MatchesMagic(pending.Mime, chunk.AsSpan(0, length)))
        {
            return "the file is not the image it says it is";
        }

        if (pending.Bytes + length > AssistantChannelImageLimits.MaxBytes)
        {
            return $"the image is bigger than {AssistantChannelImageLimits.MaxBytes / (1024 * 1024)} MB";
        }

        if (_buffered + length > MaxBufferedBytes)
        {
            return "too many images are on their way at once";
        }

        pending.Chunks.Add(chunk[..length]);
        pending.Bytes += length;
        pending.Touched = _time.GetTimestamp();
        _pending[id] = pending;
        _buffered += length;
        _sweep ??= _time.CreateTimer(_ => _SweepNow(), null, Expiry, Expiry);
        return null;
    }

    // Under _gate.
    private void _Drop(string id)
    {
        if (_pending.Remove(id, out var pending))
        {
            _buffered -= pending.Bytes;
        }

        if (_refusedIds.Count >= _MaxRememberedRefusals)
        {
            _refusedIds.Clear();
        }

        _refusedIds.Add(id);
        _StopSweepWhenIdle();
    }

    private void _SweepNow()
    {
        List<string> expired;
        lock (_gate)
        {
            expired = _Sweep();
        }

        _ReportExpired(expired);
    }

    private void _ReportExpired(List<string> expired)
    {
        foreach (var id in expired)
        {
            _refused(id, "the image expired before it was sent");
        }
    }

    // Under _gate: everything quiet for Expiry goes, complete or not.
    private List<string> _Sweep()
    {
        List<string> expired = [.. _pending.Where(entry => _time.GetElapsedTime(entry.Value.Touched) >= Expiry).Select(entry => entry.Key)];
        foreach (var id in expired)
        {
            _Drop(id);
        }

        return expired;
    }

    // Under _gate: no timer runs for a phone with nothing waiting.
    private void _StopSweepWhenIdle()
    {
        if (_pending.Count == 0)
        {
            _sweep?.Dispose();
            _sweep = null;
        }
    }

    // The format's own signature at the start of the file, so a declared type is not taken on trust.
    private static bool _MatchesMagic(string mime, ReadOnlySpan<byte> start) => mime switch
    {
        "image/jpeg" => start is [0xFF, 0xD8, 0xFF, ..],
        "image/png" => start is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..],
        "image/webp" => start is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..],
        _ => false,
    };

    private sealed class Pending(string mime, int total)
    {
        public string Mime { get; } = mime;

        public int Total { get; } = total;

        public List<byte[]> Chunks { get; } = [];

        public long Bytes { get; set; }

        public long Touched { get; set; }

        public bool Complete => Chunks.Count == Total;

        public byte[] Join()
        {
            var all = new byte[Bytes];
            var at = 0;
            foreach (var chunk in Chunks)
            {
                chunk.CopyTo(all, at);
                at += chunk.Length;
            }

            return all;
        }
    }
}
