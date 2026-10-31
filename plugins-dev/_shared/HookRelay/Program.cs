using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

try
{
    var target = args.Length == 2 && args[0] == "--environment" ? Environment.GetEnvironmentVariable("COCKPIT_SUBAGENT_FILE") : args.FirstOrDefault();
    if (args.Length != 2 || target is null || !Path.IsPathFullyQualified(target))
    {
        return;
    }
    var input = new char[65536];
    var count = Console.In.ReadBlock(input, 0, input.Length);
    if (count == input.Length || JsonNode.Parse(new string(input, 0, count)) is not JsonObject data)
    {
        return;
    }
    string? Text(string key) => data[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? data[key]?.GetValue<string>() : null;
    var agent = Text("agent_id");
    var owner = Text("session_id");
    var kind = Text("hook_event_name");
    if (agent is null || owner is null || !Regex.IsMatch(agent, "^[a-zA-Z0-9_-]{1,128}$")
        || !Regex.IsMatch(owner, "^[a-zA-Z0-9_-]{1,128}$") || kind is not ("SubagentStart" or "SubagentStop"))
    {
        return;
    }
    var parentPath = Text("transcript_path");
    if (args[1] == "codex")
    {
        using var parentFile = new FileStream(parentPath ?? "", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(parentFile.Length, 262144)];
        parentFile.ReadExactly(bytes);
        var end = Array.IndexOf(bytes, (byte)'\n');
        var metadata = end < 0 ? null : JsonNode.Parse(bytes.AsSpan(0, end)) as JsonObject;
        var payload = metadata?["payload"] as JsonObject;
        if (payload?["id"]?.GetValue<string>() != owner || payload?["parent_thread_id"]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            return;
        }
    }
    else if (parentPath is null || Path.GetFileNameWithoutExtension(parentPath) != owner
        || parentPath.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }
    var journal = target;
    using var mutex = new Mutex(false, "cockpit-child-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(journal))));
    var acquired = false;
    try
    {
        try { acquired = mutex.WaitOne(300); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            return;
        }
        JsonObject snapshot;
        try { snapshot = JsonNode.Parse(File.ReadAllText(journal)) as JsonObject ?? new(); }
        catch (Exception) { snapshot = new(); }
        if (snapshot["session_id"]?.GetValue<string>() is { } knownOwner && knownOwner != owner)
        {
            return;
        }
        if (snapshot["session_id"]?.GetValue<string>() is null)
        {
            snapshot = new JsonObject { ["session_id"] = owner, ["entries"] = new JsonArray() };
        }
        snapshot["transcript_path"] = Text("transcript_path");
        snapshot["cwd"] = Text("cwd");
        var entries = snapshot["entries"] as JsonArray ?? new JsonArray();
        snapshot["entries"] = entries;
        var entry = entries.OfType<JsonObject>().FirstOrDefault(item => item["agent_id"]?.GetValue<string>() == agent);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (entry is null)
        {
            entry = new JsonObject { ["agent_id"] = agent, ["revision"] = Guid.NewGuid().ToString("N") };
            entries.Add(entry);
        }
        entry["agent_type"] = Text("agent_type");
        if (kind == "SubagentStart")
        {
            entry["started_at_ms"] = now;
            entry["stopped_at_ms"] = null;
            entry["revision"] = Guid.NewGuid().ToString("N");
        }
        else
        {
            entry["stopped_at_ms"] = now;
            if (Text("agent_transcript_path") is { Length: < 4096 } path)
            {
                entry["child_path"] = path;
            }
        }
        entry["observed_at_ms"] = now;
        while (entries.Count > 128)
        {
            var oldest = entries.OfType<JsonObject>().MinBy(item => item["observed_at_ms"]?.GetValue<long>() ?? 0);
            entries.Remove(oldest);
        }
        void Save(string path, JsonObject value)
        {
            File.WriteAllText(path + ".tmp", value.ToJsonString());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(path + ".tmp", path, overwrite: true);
        }
        Save(journal, snapshot);
        if (args[1] == "codex")
        {
            var status = journal[..^".children.json".Length];
            Save(status, new JsonObject { ["session_id"] = owner, ["transcript_path"] = Text("transcript_path"), ["cwd"] = Text("cwd") });
        }
    }
    finally
    {
        if (acquired)
        {
            mutex.ReleaseMutex();
        }
    }
}
catch (Exception)
{
    // Observations never change the provider's outcome.
}
