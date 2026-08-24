using Avalonia.Threading;

namespace Cockpit.App.Services;

// AC-642 (valkuil 1): memoised so a streaming reply's ~30fps repaint never touches disk directly. `Resolve`
// answers from the cache and kicks off a background probe on a miss; negatives expire after `NegativeTtl`
// since an agent sometimes announces a file before writing it. `Exists` is a swappable seam for tests.
internal static class FilePathResolver
{
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    // ponytail: dumb full-clear eviction past this many entries, not an LRU — a transcript rarely names more
    // than a handful of distinct paths per session. Upgrade to an LRU if a long session ever measurably thrashes.
    private const int MaxCacheEntries = 2000;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string BasePath, string Candidate), (string? Full, DateTimeOffset At)> Cache = [];

    // One shared probe per key, not per caller: two MarkdownView instances naming the same unresolved path
    // around the same moment used to leave the second caller's callback dropped until the next rebuild.
    // Every waiter registered while a probe is in flight is notified when it lands.
    private static readonly Dictionary<(string BasePath, string Candidate), List<Action>> Pending = [];

    internal static Func<string, bool> Exists = path => File.Exists(path) || Directory.Exists(path);

    // The full path once known; null while the answer is still pending or the candidate does not exist.
    // `onSettled` fires once, on the UI thread, the moment a pending probe lands — never stored beyond that
    // one in-flight probe, shared by every caller that arrived while it was running.
    internal static string? Resolve(string candidate, string? basePath, Action onSettled)
    {
        if (string.IsNullOrEmpty(basePath) && !Path.IsPathRooted(candidate))
        {
            return null;
        }

        var key = (basePath ?? string.Empty, candidate);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                if (entry.Full is not null)
                {
                    return entry.Full;
                }

                if (DateTimeOffset.UtcNow - entry.At < NegativeTtl)
                {
                    return null;
                }
            }

            if (Pending.TryGetValue(key, out var waiters))
            {
                waiters.Add(onSettled);
                return null; // already being probed — this caller joins the same in-flight probe
            }

            Pending[key] = [onSettled];
        }

        _ = _ProbeAsync(key);
        return null;
    }

    private static async Task _ProbeAsync((string BasePath, string Candidate) key)
    {
        var full = await Task.Run(() =>
        {
            var resolved = Path.IsPathRooted(key.Candidate) ? key.Candidate : Path.Combine(key.BasePath, key.Candidate);
            return Exists(resolved) ? resolved : null;
        });

        List<Action> waiters;
        lock (Gate)
        {
            waiters = Pending.TryGetValue(key, out var list) ? list : [];
            Pending.Remove(key);
            if (Cache.Count >= MaxCacheEntries)
            {
                Cache.Clear();
            }

            Cache[key] = (full, DateTimeOffset.UtcNow);
        }

        foreach (var waiter in waiters)
        {
            Dispatcher.UIThread.Post(waiter);
        }
    }
}
