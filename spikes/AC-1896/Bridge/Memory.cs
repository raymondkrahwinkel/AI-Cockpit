// AC-1896 spike: what a room costs in memory and time, one Jint engine per room with a 2,000-line note seeded and its mirror built.
using System.Diagnostics;

internal static class Memory
{
    public static int Run(string bundle, int rooms)
    {
        var note = G4.BigNote();
        var keep = new List<JintRoom>();
        long Private() => Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
        GC.Collect();
        Console.WriteLine($"     start: private {Private()} MiB, managed {GC.GetTotalMemory(true) / 1048576} MiB");
        for (var i = 1; i <= rooms; i++)
        {
            var sw = Stopwatch.StartNew();
            var room = new JintRoom(bundle, (uint)i);
            var load = sw.ElapsedMilliseconds;
            room.Call("seedMarkdown", note);
            room.TakeUpdates();
            room.Call("loadMirror");
            keep.Add(room);
            Console.WriteLine($"     room {i}: engine+bundle {load} ms, seed+mirror {sw.ElapsedMilliseconds - load} ms; private {Private()} MiB, managed {GC.GetTotalMemory(true) / 1048576} MiB");
        }
        GC.KeepAlive(keep);
        return 0;
    }
}
