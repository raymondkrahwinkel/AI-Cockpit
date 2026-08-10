using Avalonia.Threading;

namespace Cockpit.App.Services;

// Whether a `MarkdownView` code-span is a real file, memoised so a streaming reply's ~30fps repaint never
// touches disk directly (AC-642, valkuil 1): `Resolve` always answers from the cache and, on a miss, kicks
// off a background probe and returns null. A positive answer is kept; a negative one expires after
// `NegativeTtl`, because an agent sometimes announces a file a moment before it writes it. `Exists` is a
// swappable seam so `FilePathResolverTests` never touches the real disk.
internal static class FilePathResolver
{
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    // ponytail: dumb full-clear eviction past this many entries, not an LRU — a transcript rarely names more
    // than a handful of distinct paths per session. Upgrade to an LRU if a long session ever measurably thrashes.
    private const int MaxCacheEntries = 2000;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string BasePath, string Candidate), (string? Full, DateTimeOffset At)> Cache = [];
    private static readonly HashSet<(string BasePath, string Candidate)> Pending = [];

    internal static Func<string, bool> Exists = path => File.Exists(path) || Directory.Exists(path);

    // The full path once known; null while the answer is still pending or the candidate does not exist.
    // `onSettled` fires once, on the UI thread, the moment a pending probe lands — never stored beyond that.
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

            if (!Pending.Add(key))
            {
                return null; // already being probed — this repaint gets the earlier one's answer
            }
        }

        _ = _ProbeAsync(key, onSettled);
        return null;
    }

    private static async Task _ProbeAsync((string BasePath, string Candidate) key, Action onSettled)
    {
        var full = await Task.Run(() =>
        {
            var resolved = Path.IsPathRooted(key.Candidate) ? key.Candidate : Path.Combine(key.BasePath, key.Candidate);
            return Exists(resolved) ? resolved : null;
        });

        lock (Gate)
        {
            Pending.Remove(key);
            if (Cache.Count >= MaxCacheEntries)
            {
                Cache.Clear();
            }

            Cache[key] = (full, DateTimeOffset.UtcNow);
        }

        Dispatcher.UIThread.Post(onSettled);
    }
}
