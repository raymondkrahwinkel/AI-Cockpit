using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Shared;

internal sealed class TtyChildSnapshot
{
    private readonly Lock _gate = new();
    private readonly ProviderChildLedger _ledger = new();
    private IReadOnlyList<TtyChildSource> _sources = [];
    private string? _owner;

    public PluginChildTranscriptPage ReadPage(string root, string? status, bool codex, string agent, string? revision, string? cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            TtyChildSource? source = Read(root, status, codex).FirstOrDefault(source => source.Task.AgentId == agent && source.Task.SourceRevision == revision);
            if (source is null)
            {
                return PluginChildTranscriptPage.Unavailable("child source or revision changed");
            }
            PluginChildTranscriptPage page = JsonlChildTranscript.Read(source.Path, agent, source.OwnerId, cursor, cancellationToken);
            return Read(root, status, codex).Any(current => current.Task.AgentId == agent && current.Task.SourceRevision == revision)
                ? page : PluginChildTranscriptPage.Unavailable("child source or revision changed during read");
        }
    }

    public IReadOnlyList<TtyChildSource> Read(string stateRoot, string? statusFile, bool codex)
    {
        lock (_gate)
        {
            try
            {
                return _Read(stateRoot, statusFile, codex);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                _sources = [];
                return _sources;
            }
        }
    }

    private IReadOnlyList<TtyChildSource> _Read(string root, string? status, bool codex)
    {
        if (status is null)
        {
            return [];
        }
        using JsonDocument? ownerDocument = _Json(status);
        using JsonDocument? journalDocument = _Json(TtyChildHooks.Journal(status));
        JsonElement ownerData = ownerDocument?.RootElement ?? journalDocument?.RootElement ?? default;
        string? parentPath = Text(ownerData, "transcript_path");
        string? owner = Text(ownerData, "session_id") ?? (parentPath is null ? null : Path.GetFileNameWithoutExtension(parentPath));
        string[] codexPaths = codex && Directory.Exists(Path.Combine(root, "sessions"))
            ? Directory.EnumerateFiles(Path.Combine(root, "sessions"), "rollout-*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).OrderByDescending(File.GetLastWriteTimeUtc).Take(256).ToArray()
            : [];
        if (codex && parentPath is null && owner is not null)
        {
            foreach (var candidate in codexPaths.Where(path => SafePath(path, root)))
            {
                using JsonDocument? metadata = FirstRecord(candidate);
                if (metadata?.RootElement.TryGetProperty("payload", out var payload) == true && Text(payload, "id") == owner && Text(payload, "parent_thread_id") is null)
                {
                    parentPath = candidate;
                    break;
                }
            }
        }
        if (parentPath is null || owner is null || !SafePath(parentPath, root))
        {
            return [];
        }
        using JsonDocument? parentRecord = FirstRecord(parentPath);
        if (codex && (parentRecord?.RootElement.TryGetProperty("payload", out var parentPayload) != true || Text(parentPayload, "id") != owner || Text(parentPayload, "parent_thread_id") is not null))
        {
            return [];
        }
        if (!codex && Path.GetFileNameWithoutExtension(parentPath) != owner)
        {
            return [];
        }
        if (_owner != owner)
        {
            _ledger.Clear();
            _owner = owner;
        }
        JsonElement journal = journalDocument?.RootElement ?? default;
        var entries = new Dictionary<string, JsonElement>();
        if (Text(journal, "session_id") == owner && journal.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(128))
            {
                if (Text(item, "agent_id") is { } id && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,128}$"))
                {
                    entries[id] = item;
                }
            }
        }
        var paths = new Dictionary<string, string>();
        if (!codex)
        {
            string directory = Path.Combine(Path.GetDirectoryName(parentPath) ?? "", owner, "subagents");
            if (Directory.Exists(directory) && SafePath(directory, root))
            {
                foreach (var meta in Directory.EnumerateFiles(directory, "agent-*.meta.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(128))
                {
                    paths[Path.GetFileName(meta)[6..^10]] = meta[..^10] + ".jsonl";
                }
            }
            foreach (var id in entries.Keys)
            {
                paths[id] = Path.Combine(directory, $"agent-{id}.jsonl");
            }
        }
        else
        {
            string sessions = Path.Combine(root, "sessions");
            if (Directory.Exists(sessions))
            {
                foreach (var path in codexPaths)
                {
                    if (!SafePath(path, root))
                    {
                        continue;
                    }
                    using JsonDocument? record = FirstRecord(path);
                    if (record?.RootElement.TryGetProperty("payload", out var payload) == true && Text(payload, "parent_thread_id") == owner && Text(payload, "id") is { } id)
                    {
                        paths[id] = path;
                    }
                }
            }
        }
        var sources = new List<TtyChildSource>();
        foreach (var pair in paths.Take(128))
        {
            string id = pair.Key;
            string path = pair.Value;
            if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,128}$") || !SafePath(path, root))
            {
                continue;
            }
            using JsonDocument? record = FirstRecord(path);
            JsonElement first = record?.RootElement ?? default;
            if (!codex && (Text(first, "agentId") != id || Text(first, "sessionId") != owner || !Flag(first, "isSidechain")))
            {
                continue;
            }
            entries.TryGetValue(id, out var observed);
            using JsonDocument? metaDocument = codex ? null : _Json(path[..^6] + ".meta.json");
            JsonElement meta = metaDocument?.RootElement ?? default;
            long? start = Number(observed, "started_at_ms");
            long? stop = Number(observed, "stopped_at_ms");
            bool finished = !codex && Flag(meta, "finished") && (start is null || new DateTimeOffset(File.GetLastWriteTimeUtc(path[..^6] + ".meta.json")).ToUnixTimeMilliseconds() >= start);
            if (codex)
            {
                finished = _CodexFinished(path) && (start is null || new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() >= start);
            }
            string state = finished ? "completed" : start is not null && stop is null ? "running" : "unknown";
            var task = new PluginBackgroundTask(id, PluginBackgroundTaskKind.SubAgent, Text(meta, "description") ?? id)
            {
                AgentId = id, ParentTaskId = owner, ParentToolUseId = Text(meta, "toolUseId"), State = state,
                AgentType = Text(meta, "agentType") ?? Text(observed, "agent_type"),
                SourceRevision = (Text(observed, "revision") ?? File.GetCreationTimeUtc(path).Ticks.ToString()) + ":" + Text(meta, "toolUseId"),
                StartedAtUnixMilliseconds = start,
                DurationMilliseconds = finished && start is >= 0 && stop >= start ? stop - start : null,
            };
            _ledger.Remember(task);
            sources.Add(new(task, path, owner, Text(ownerData, "cwd") ?? Path.GetDirectoryName(parentPath) ?? ""));
        }
        IReadOnlyList<PluginBackgroundTask> snapshot = _ledger.Snapshot([.. sources.Where(source => source.Task.State == "running").Select(source => source.Task)]);
        _sources = [.. sources.Select(source => source with { Task = snapshot.First(task => task.TaskId == source.Task.TaskId) })];
        return _sources;
    }

    private static bool _CodexFinished(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = Math.Max(0, file.Length - 65536);
        using var reader = new StreamReader(file);
        string tail = reader.ReadToEnd();
        var finished = false;
        foreach (var line in tail.Split('\n').Skip(file.Length > 65536 ? 1 : 0).SkipLast(1))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement value = document.RootElement;
                if (Text(value, "type") == "event_msg" && value.TryGetProperty("payload", out var payload))
                {
                    string? type = Text(payload, "type");
                    if (type is "task_started" or "task_complete" or "turn_complete")
                    {
                        finished = type != "task_started";
                    }
                }
            }
            catch (JsonException) { }
        }
        return finished;
    }
    public static bool SafePath(string path, string root)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }
        for (FileSystemInfo? entry = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full); entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (Path.GetFullPath(entry.FullName).TrimEnd(Path.DirectorySeparatorChar) == fullRoot.TrimEnd(Path.DirectorySeparatorChar))
            {
                return true;
            }
        }
        return false;
    }
    public static JsonDocument? FirstRecord(string path)
    {
        try
        {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(file.Length, 262144)];
        file.ReadExactly(buffer);
        int end = Array.IndexOf(buffer, (byte)'\n');
        return end < 0 ? null : JsonDocument.Parse(buffer.AsMemory(0, end));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
    private static JsonDocument? _Json(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 1048576 ? JsonDocument.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;
    private static bool Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
