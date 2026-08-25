using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 1: shapes an Argo CD Application into the handful of fields an agent needs, instead of the
// full status get_resource would return (~51 KB per app). Works against JsonElement, same pattern as
// ResourceListSummary, so it can be tested against a literal payload.
internal static class ArgoApplicationSummary
{
    private const int ShortRevisionLength = 7;

    public static JsonNode SummarizeList(RawKubernetesList list)
    {
        var items = new JsonArray();
        foreach (var app in list.Items ?? [])
        {
            items.Add(_SummarizeListItem(app.Data));
        }

        return new JsonObject { ["count"] = items.Count, ["applications"] = items };
    }

    public static JsonNode SummarizeApp(RawKubernetesObject app) => _SummarizeApp(app.Data);

    public static JsonNode SummarizeHistory(RawKubernetesObject app)
    {
        var history = new JsonArray();
        if (_Get(app.Data, "status", "history") is { ValueKind: JsonValueKind.Array } entries)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                history.Add(new JsonObject
                {
                    ["revision"] = _String(_Get(entry, "revision")),
                    ["deployedAt"] = _String(_Get(entry, "deployedAt")),
                    ["initiatedBy"] = _InitiatedBy(entry, "initiatedBy"),
                });
            }
        }

        return new JsonObject { ["name"] = _String(_Get(app.Data, "metadata", "name")), ["history"] = history };
    }

    public static JsonNode SummarizeLastSync(RawKubernetesObject app)
    {
        var name = _String(_Get(app.Data, "metadata", "name"));
        if (_Get(app.Data, "status", "operationState") is not { } operationState)
        {
            return new JsonObject { ["name"] = name, ["operationState"] = null };
        }

        var resources = new JsonArray();
        if (_Get(operationState, "syncResult", "resources") is { ValueKind: JsonValueKind.Array } syncResources)
        {
            foreach (var resource in syncResources.EnumerateArray())
            {
                resources.Add(new JsonObject
                {
                    ["kind"] = _String(_Get(resource, "kind")),
                    ["name"] = _String(_Get(resource, "name")),
                    ["namespace"] = _String(_Get(resource, "namespace")),
                    ["status"] = _String(_Get(resource, "status")),
                    ["message"] = _String(_Get(resource, "message")),
                });
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["phase"] = _String(_Get(operationState, "phase")),
            ["message"] = _String(_Get(operationState, "message")),
            ["startedAt"] = _String(_Get(operationState, "startedAt")),
            ["finishedAt"] = _String(_Get(operationState, "finishedAt")),
            ["initiatedBy"] = _InitiatedBy(operationState, "operation", "initiatedBy"),
            ["resources"] = resources,
        };
    }

    private static JsonObject _SummarizeListItem(Dictionary<string, JsonElement> data)
    {
        var revision = _String(_Get(data, "status", "sync", "revision"));
        return new JsonObject
        {
            ["name"] = _String(_Get(data, "metadata", "name")),
            ["project"] = _String(_Get(data, "spec", "project")),
            ["syncStatus"] = _String(_Get(data, "status", "sync", "status")),
            ["health"] = _String(_Get(data, "status", "health", "status")),
            ["sourceType"] = _String(_Get(data, "status", "sourceType")),
            ["revision"] = _ShortRevision(revision),
            ["outOfSyncCount"] = _OutOfSyncCount(data),
        };
    }

    private static JsonObject _SummarizeApp(Dictionary<string, JsonElement> data)
    {
        var source = _Get(data, "spec", "source") ?? _FirstSource(data);
        return new JsonObject
        {
            ["name"] = _String(_Get(data, "metadata", "name")),
            ["project"] = _String(_Get(data, "spec", "project")),
            ["source"] = source is { } found ? _Source(found) : null,
            ["destination"] = new JsonObject
            {
                ["server"] = _String(_Get(data, "spec", "destination", "server")),
                ["name"] = _String(_Get(data, "spec", "destination", "name")),
                ["namespace"] = _String(_Get(data, "spec", "destination", "namespace")),
            },
            // Whether Argo will re-apply Git on its own, and reconcile away a manual edit — see the
            // AC-576 pitfall this exists for: it is not cosmetic, it decides whether an intervention holds.
            ["autoSync"] = _Get(data, "spec", "syncPolicy", "automated") is not null,
            ["selfHeal"] = _Bool(_Get(data, "spec", "syncPolicy", "automated", "selfHeal")),
            ["syncStatus"] = _String(_Get(data, "status", "sync", "status")),
            ["revision"] = _String(_Get(data, "status", "sync", "revision")),
            ["health"] = _String(_Get(data, "status", "health", "status")),
            ["sourceType"] = _String(_Get(data, "status", "sourceType")),
            ["resources"] = _Resources(data),
        };
    }

    private static JsonObject _Source(JsonElement source) => new()
    {
        ["repoURL"] = _String(_Get(source, "repoURL")),
        ["path"] = _String(_Get(source, "path")),
        ["targetRevision"] = _String(_Get(source, "targetRevision")),
    };

    private static JsonElement? _FirstSource(Dictionary<string, JsonElement> data) =>
        _Get(data, "spec", "sources") is { ValueKind: JsonValueKind.Array } sources && sources.GetArrayLength() > 0
            ? sources[0]
            : null;

    private static JsonArray _Resources(Dictionary<string, JsonElement> data)
    {
        var resources = new JsonArray();
        if (_Get(data, "status", "resources") is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var resource in array.EnumerateArray())
            {
                resources.Add(new JsonObject
                {
                    ["kind"] = _String(_Get(resource, "kind")),
                    ["name"] = _String(_Get(resource, "name")),
                    ["namespace"] = _String(_Get(resource, "namespace")),
                    ["syncStatus"] = _String(_Get(resource, "status")),
                    // Only present when it is not Healthy — a resource entry on a healthy Application
                    // carries no "health" property at all, so this is null far more often than not.
                    ["health"] = _String(_Get(resource, "health", "status")),
                });
            }
        }

        return resources;
    }

    private static int _OutOfSyncCount(Dictionary<string, JsonElement> data)
    {
        if (_Get(data, "status", "resources") is not { ValueKind: JsonValueKind.Array } array)
        {
            return 0;
        }

        return array.EnumerateArray().Count(resource => _String(_Get(resource, "status")) == "OutOfSync");
    }

    private static string? _ShortRevision(string? revision) =>
        revision is { Length: > ShortRevisionLength } ? revision[..ShortRevisionLength] : revision;

    // `initiatedBy.username` for a human-triggered sync, "automated sync" when Argo's own controller did
    // it (`initiatedBy.automated: true`), null when neither is present.
    private static string? _InitiatedBy(JsonElement element, params string[] path)
    {
        if (_Get(element, path) is not { } initiatedBy)
        {
            return null;
        }

        return _String(_Get(initiatedBy, "username")) ?? (_Bool(_Get(initiatedBy, "automated")) ? "automated sync" : null);
    }

    private static JsonElement? _Get(Dictionary<string, JsonElement> data, params string[] path)
    {
        if (path.Length == 0 || !data.TryGetValue(path[0], out var root))
        {
            return null;
        }

        return path.Length == 1 ? root : _Get(root, path[1..]);
    }

    private static JsonElement? _Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? _String(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool _Bool(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.True or JsonValueKind.False } value && value.GetBoolean();
}
