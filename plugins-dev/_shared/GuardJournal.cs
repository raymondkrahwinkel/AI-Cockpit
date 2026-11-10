using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugins.Shared;

internal sealed record GuardViolation(string Mode, string Tool, string Target, bool Denied, string Kind);

internal sealed record GuardJournalEntry(string PaneId, GuardViolation Violation);

internal static class GuardJournal
{
    public static string PathFor(string childJournal) => childJournal + ".guards.jsonl";

    public static void Record(string? childJournal, string? paneId, IReadOnlyList<GuardViolation> violations, TextWriter stderr)
    {
        if (childJournal is null || paneId is null || violations.Count == 0)
        {
            return;
        }
        try
        {
            var path = PathFor(childJournal);
            using var mutex = _Mutex(path);
            if (!_Acquire(mutex))
            {
                stderr.WriteLine("cockpit-guard: notification journal is busy");
                return;
            }
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                foreach (var violation in violations)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new GuardJournalEntry(paneId, violation)));
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"cockpit-guard: notification journal failed: {exception.Message}");
        }
    }

    public static IReadOnlyList<GuardJournalEntry> Read(string path, ref long position)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        using var mutex = _Mutex(path);
        if (!_Acquire(mutex))
        {
            return [];
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Min(position, stream.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var entries = new List<GuardJournalEntry>();
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    if (JsonSerializer.Deserialize<GuardJournalEntry>(line) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A malformed observation must not hide the following events.
                }
            }
            position = stream.Position;
            return entries;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static Mutex _Mutex(string path) => new(false, "cockpit-guard-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))));

    private static bool _Acquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(300);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
