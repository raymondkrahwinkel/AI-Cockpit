using System.Text;

namespace Cockpit.Plugin.Depot.Notes.Live;

// One client's awareness state: its id, its clock and its state as JSON. The text "null" is the state of someone who left.
internal sealed record DepotAwarenessEntry(ulong ClientId, ulong Clock, string State);

// The y-protocols awareness update, version 1 (the bytes of the hub's `Presence` and `presence`): a varuint count, then per client a
// varuint client id, a varuint clock and the state as a varuint-prefixed UTF-8 JSON string. Docs/notes-hub-protocol.md section 5.4.
internal static class DepotAwarenessCodec
{
    // A web room has a handful of clients; a count far above that is not an awareness update.
    private const int MaxEntries = 1024;

    public static byte[] Encode(IReadOnlyList<DepotAwarenessEntry> entries)
    {
        using var bytes = new MemoryStream();
        WriteVarUint(bytes, (ulong)entries.Count);
        foreach (var entry in entries)
        {
            WriteVarUint(bytes, entry.ClientId);
            WriteVarUint(bytes, entry.Clock);
            var state = Encoding.UTF8.GetBytes(entry.State);
            WriteVarUint(bytes, (ulong)state.Length);
            bytes.Write(state);
        }

        return bytes.ToArray();
    }

    // Throws FormatException for bytes that are not an update: cut short, a count or length beyond what is there, or bad UTF-8.
    public static IReadOnlyList<DepotAwarenessEntry> Decode(ReadOnlySpan<byte> data)
    {
        var at = 0;
        var count = ReadVarUint(data, ref at);
        if (count > MaxEntries)
        {
            throw new FormatException("The awareness update lists too many clients.");
        }

        var entries = new List<DepotAwarenessEntry>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            var client = ReadVarUint(data, ref at);
            var clock = ReadVarUint(data, ref at);
            var length = ReadVarUint(data, ref at);
            if (length > (ulong)(data.Length - at))
            {
                throw new FormatException("The awareness update is cut short.");
            }

            entries.Add(new DepotAwarenessEntry(client, clock, new UTF8Encoding(false, true).GetString(data.Slice(at, (int)length))));
            at += (int)length;
        }

        return entries;
    }

    private static void WriteVarUint(Stream output, ulong value)
    {
        while (value > 0x7f)
        {
            output.WriteByte((byte)(0x80 | (value & 0x7f)));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static ulong ReadVarUint(ReadOnlySpan<byte> data, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= data.Length)
            {
                throw new FormatException("The awareness update is cut short.");
            }

            var next = data[at++];
            value |= (ulong)(next & 0x7f) << shift;
            if (next < 0x80)
            {
                return value;
            }
        }

        throw new FormatException("The awareness update has a number that is too long.");
    }
}
