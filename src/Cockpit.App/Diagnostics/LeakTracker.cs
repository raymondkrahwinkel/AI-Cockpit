#if DEBUG
using System.Text;

namespace Cockpit.App.Diagnostics;

// DEBUG-only weak-reference tracking for heap-dump suspects; rising `leaktrack` counts identify survivors to
// inspect with gcroot. DiagnosticsBackgroundService logs it every ~10 s, while Release compiles it out entirely.
internal static class LeakTracker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<WeakReference>> ByType = new();

    public static void Register(object instance)
    {
        var type = instance.GetType().Name;
        lock (Gate)
        {
            if (!ByType.TryGetValue(type, out var list))
            {
                ByType[type] = list = [];
            }

            list.Add(new WeakReference(instance));
        }
    }

    // Drops all tracking, so a test can measure only the instances it creates after this point (the tracker is
    // process-global, so counts otherwise bleed across tests).
    public static void Reset()
    {
        lock (Gate)
        {
            ByType.Clear();
        }
    }

    // Full GC, then the number of the given type still alive. For tests/assertions.
    public static int AliveCount(string typeName)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        lock (Gate)
        {
            if (!ByType.TryGetValue(typeName, out var list))
            {
                return 0;
            }

            list.RemoveAll(w => !w.IsAlive);
            return list.Count;
        }
    }

    // Forces a full GC, prunes dead refs, and returns "leaktrack Type=alive Type=alive …" ordered by type.
    public static string ReportAfterGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sb = new StringBuilder("leaktrack");
        lock (Gate)
        {
            foreach (var type in ByType.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var list = ByType[type];
                list.RemoveAll(w => !w.IsAlive);
                sb.Append(' ').Append(type).Append('=').Append(list.Count);
            }
        }

        return sb.ToString();
    }
}
#endif
