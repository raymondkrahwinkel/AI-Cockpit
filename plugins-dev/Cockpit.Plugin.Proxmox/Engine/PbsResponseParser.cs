using System.Text.Json;

namespace Cockpit.Plugin.Proxmox.Engine;

internal static class PbsResponseParser
{
    public static IReadOnlyList<PbsDatastore> Datastores(JsonElement data) => Array(data).Select(store => new PbsDatastore(
        String(store, "store"), Long(store, "total"), Long(store, "used"), StringOrNull(store, "gc-status"), StringOrNull(store, "verify-status"))).ToList();

    public static IReadOnlyList<PbsBackup> Backups(string datastore, JsonElement data) => Array(data).Select(snapshot => new PbsBackup(
        datastore, String(snapshot, "backup-type"), String(snapshot, "backup-id"), Long(snapshot, "backup-time"), Long(snapshot, "size"), StringOrNull(snapshot, "verification"), Bool(snapshot, "protected"))).ToList();

    public static IReadOnlyList<PbsTask> Tasks(JsonElement data) => Array(data).Select(task => new PbsTask(
        String(task, "upid"), String(task, "worker_type"), StringOrNull(task, "status"), String(task, "user"), Long(task, "starttime"), NullableLong(task, "endtime"))).ToList();

    public static IReadOnlyList<PbsJob> Jobs(JsonElement verify, JsonElement prune, JsonElement sync) =>
        Array(verify).Select(job => new PbsJob("verify", String(job, "id"), StringOrNull(job, "store"), StringOrNull(job, "schedule"), StringOrNull(job, "last-run-state")))
            .Concat(Array(prune).Select(job => new PbsJob("prune", String(job, "id"), StringOrNull(job, "store"), StringOrNull(job, "schedule"), StringOrNull(job, "last-run-state"))))
            .Concat(Array(sync).Select(job => new PbsJob("sync", String(job, "id"), StringOrNull(job, "store"), StringOrNull(job, "schedule"), StringOrNull(job, "last-run-state")))).ToList();

    private static IEnumerable<JsonElement> Array(JsonElement data) => data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText() : string.Empty;
    private static string? StringOrNull(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? String(value, name) : null;
    private static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetInt64() : 0;
    private static long? NullableLong(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetInt64() : null;
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
