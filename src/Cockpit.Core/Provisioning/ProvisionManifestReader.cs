using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Provisioning;

// The outcome of reading one manifest: a validated object, or the reason the whole document is refused. `Skipped`
// lists what the reader stripped (unknown fields, values that do not travel) so nothing falls away unseen.
public sealed record ProvisionReadResult(ProvisionManifest? Manifest, IReadOnlyList<ProvisionNote> Skipped, string? Error);

// AC-1505 (DB-2.5a): validates a "cockpit.provision" document before anything is applied (manifest-v1 §5.2, §7, §8).
// Fails closed on policy and connections, open on preferences: an unknown field elsewhere is stripped and reported.
public static partial class ProvisionManifestReader
{
    public const string Format = "cockpit.provision";

    public const int FormatVersion = 1;

    public const string DefaultMode = "default";

    public const string LockedMode = "locked";

    public const string SkippedUnknown = "skipped-unknown";

    public const string SkippedNotPortable = "skipped-not-portable";

    private static readonly string[] Origins = ["org", "set", "profile"];

    private static readonly string[] ManagingOrigins = ["org", "set"];

    private static readonly string[] Modes = [DefaultMode, LockedMode];

    private static readonly string[] PermissionModes = ["plan", "default", "acceptEdits", "bypassPermissions"];

    private static readonly string[] Switches = ["allowed", "forbidden"];

    private static readonly string[] IssuerKinds = ["depot", "static"];

    private static readonly string[] StoreAuth = ["none", "token-required"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static ProvisionReadResult Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            return _Refused($"it is not valid JSON ({exception.Message})");
        }

        if (root is not JsonObject document)
        {
            return _Refused("it is not a JSON object");
        }

        if (_HeaderError(document) is { } headerError)
        {
            return _Refused(headerError);
        }

        var skipped = new List<ProvisionNote>();
        if (_StripPaths(document, string.Empty, skipped) is { } leak)
        {
            return _Refused(leak);
        }

        ProvisionManifest manifest;
        try
        {
            manifest = document.Deserialize<ProvisionManifest>(Options)
                ?? throw new JsonException("the document did not contain a provisioning manifest");
        }
        catch (JsonException exception)
        {
            return _Refused($"it does not match the schema ({exception.Message})");
        }

        // Whatever the typed object does not carry back out was not in the schema: never taken over, always reported.
        if (_ReportUnknown(document, JsonSerializer.SerializeToNode(manifest, Options), string.Empty, skipped) is { } closed)
        {
            return _Refused(closed);
        }

        var errors = new List<string>();
        manifest = _Validate(manifest, errors, skipped);

        return errors.Count == 0 ? new(manifest, skipped, null) : _Refused(string.Join("; ", errors));
    }

    private static ProvisionReadResult _Refused(string reason) => new(null, [], $"The provisioning manifest was refused: {reason}.");

    private static string? _HeaderError(JsonObject document)
    {
        if (document["format"] is not JsonValue format || !format.TryGetValue<string>(out var formatName) || formatName != Format)
        {
            return $"\"format\" is not \"{Format}\"";
        }

        if (document["formatVersion"] is not JsonValue version || !version.TryGetValue<int>(out var formatVersion) || formatVersion < 1)
        {
            return "\"formatVersion\" is not a whole number of 1 or more";
        }

        if (formatVersion > FormatVersion)
        {
            return $"it asks for format {formatVersion}; this Cockpit knows up to {FormatVersion}, update Cockpit";
        }

        return document["requires"] is JsonArray { Count: > 0 } requires
            ? $"it requires extensions this Cockpit does not know ({string.Join(", ", requires.Select(name => name?.ToJsonString()))})"
            : null;
    }

    // §8, the path guard under the whitelist: a path is stripped and reported, and in policy or connections it refuses all.
    private static string? _StripPaths(JsonNode? node, string path, List<ProvisionNote> skipped)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj.ToList())
            {
                var childPath = _Join(path, name);
                if (_IsOpaque(childPath))
                {
                    continue;
                }

                // A setting without its value means nothing, so a setting that carries a path goes as a whole.
                var dropWhole = path == "settings" ? _ContainsPath(child) : child is JsonValue && _ContainsPath(child);
                if (dropWhole)
                {
                    if (_IsEnforced(childPath))
                    {
                        return $"{childPath} holds a local path, and a manifest that tries to leak is not half trusted";
                    }

                    obj.Remove(name);
                    skipped.Add(new(childPath, SkippedNotPortable, Detail: "a local path does not travel"));
                    continue;
                }

                if (_StripPaths(child, childPath, skipped) is { } error)
                {
                    return error;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                var childPath = $"{path}[{index}]";
                if (array[index] is JsonValue && _ContainsPath(array[index]))
                {
                    if (_IsEnforced(childPath))
                    {
                        return $"{childPath} holds a local path, and a manifest that tries to leak is not half trusted";
                    }

                    array.RemoveAt(index);
                    skipped.Add(new(childPath, SkippedNotPortable, Detail: "a local path does not travel"));
                }
                else if (_StripPaths(array[index], childPath, skipped) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static bool _ContainsPath(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(pair => _ContainsPath(pair.Value)),
        JsonArray array => array.Any(_ContainsPath),
        JsonValue value when value.TryGetValue<string>(out var text) => LocalPath().IsMatch(text) || _EmbeddedJsonContainsPath(text),
        _ => false,
    };

    private static bool _EmbeddedJsonContainsPath(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            return _ContainsPath(JsonNode.Parse(trimmed));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? _ReportUnknown(JsonNode? original, JsonNode? typed, string path, List<ProvisionNote> skipped)
    {
        if (original is JsonObject obj)
        {
            foreach (var (name, child) in obj)
            {
                var childPath = _Join(path, name);
                if (typed is not JsonObject known || !known.ContainsKey(name))
                {
                    if (_IsEnforced(childPath))
                    {
                        return $"{childPath} is not a field this Cockpit knows, and an unknown field in policy or connections is never ignored";
                    }

                    skipped.Add(new(childPath, SkippedUnknown, Detail: "not a field of manifest format 1"));
                    continue;
                }

                if (!_IsOpaque(childPath) && _ReportUnknown(child, known[name], childPath, skipped) is { } error)
                {
                    return error;
                }
            }
        }
        else if (original is JsonArray array && typed is JsonArray typedArray)
        {
            for (var index = 0; index < array.Count && index < typedArray.Count; index++)
            {
                if (_ReportUnknown(array[index], typedArray[index], $"{path}[{index}]", skipped) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static ProvisionManifest _Validate(ProvisionManifest manifest, List<string> errors, List<ProvisionNote> skipped)
    {
        void Check(bool valid, string error)
        {
            if (!valid)
            {
                errors.Add(error);
            }
        }

        Check(manifest.Issuer is null || IssuerKinds.Contains(manifest.Issuer.Kind), "issuer.kind is not \"depot\" or \"static\"");

        var forbidden = manifest.Plugins?.Forbidden ?? [];
        Check(manifest.Plugins?.Allowed is not { } allowed || _IsStarOrList(allowed), "plugins.allowed is not \"*\" or a list of ids");
        foreach (var required in manifest.Plugins?.Required ?? [])
        {
            Check(!forbidden.Contains(required.Id), $"plugin {required.Id} is both required and forbidden");
        }

        foreach (var store in (manifest.Stores?.Allowed ?? []).Concat(manifest.Stores?.Added ?? []))
        {
            Check(StoreAuth.Contains(store.Auth), $"store {store.Id} has an unknown auth \"{store.Auth}\"");
        }

        // §7.4: an unknown mode on a preference skips that key alone; a lock from a profile is only a default (§3.3).
        if (manifest.Settings is { } settings)
        {
            foreach (var (key, setting) in settings.ToList())
            {
                if (!Modes.Contains(setting.Mode) || !Origins.Contains(setting.By))
                {
                    settings.Remove(key);
                    skipped.Add(new($"settings.{key}", SkippedUnknown, setting.By, $"unknown mode \"{setting.Mode}\" or origin \"{setting.By}\""));
                }
                else if (setting is { Mode: LockedMode, By: "profile" })
                {
                    settings[key] = setting with { Mode = DefaultMode };
                }
            }
        }

        if (manifest.Profiles is { } profiles)
        {
            manifest = manifest with { Profiles = profiles with { Items = profiles.Items?.Select((profile, index) => _Profile(profile, index, Check, skipped)).ToList() } };
            if (profiles.AssistantProfile is { } assistantProfile)
            {
                Check(Modes.Contains(assistantProfile.Mode) && Origins.Contains(assistantProfile.By), "profiles.assistantProfile has an unknown mode or origin");
            }
        }

        foreach (var shared in manifest.Projects?.Shared ?? [])
        {
            Check(Origins.Contains(shared.By), $"shared project {shared.Slug} has an unknown origin \"{shared.By}\"");
        }

        if (manifest.Assistant?.OrgRules is { } orgRules && !ManagingOrigins.Contains(orgRules.By))
        {
            Check(orgRules.By == "profile", $"assistant.orgRules has an unknown origin \"{orgRules.By}\"");
            skipped.Add(new("assistant.orgRules", SkippedUnknown, orgRules.By, "a profile cannot set the organisation's rules"));
            manifest = manifest with { Assistant = manifest.Assistant with { OrgRules = null } };
        }

        if (manifest.Policy is { } policy)
        {
            if (!ManagingOrigins.Contains(policy.By))
            {
                Check(policy.By == "profile", $"policy has an unknown origin \"{policy.By}\"");
                skipped.Add(new("policy", SkippedUnknown, policy.By, "only an organisation or its set enforces policy"));
                manifest = manifest with { Policy = null };
                policy = new ProvisionPolicy(policy.By, null, null, null, null);
            }

            var permissions = policy.Permissions;
            Check(_IsKnownOrAbsent(permissions?.MaxPermissionMode, PermissionModes), "policy.permissions.maxPermissionMode is not a known permission mode");
            Check(_IsKnownOrAbsent(permissions?.DelegationCeiling, PermissionModes), "policy.permissions.delegationCeiling is not a known permission mode");
            Check(_IsKnownOrAbsent(permissions?.AutoApproveTools, Switches), "policy.permissions.autoApproveTools is not \"allowed\" or \"forbidden\"");
            Check(_IsKnownOrAbsent(permissions?.TerminalAccess, Switches), "policy.permissions.terminalAccess is not \"allowed\" or \"forbidden\"");
            Check(_IsKnownOrAbsent(permissions?.ShellAccess, Switches), "policy.permissions.shellAccess is not \"allowed\" or \"forbidden\"");
            Check(_IsKnownOrAbsent(policy.Backup?.IncludeCredentials, Switches), "policy.backup.includeCredentials is not \"allowed\" or \"forbidden\"");
            Check(policy.Backup?.MaxIntervalHours is null or > 0, "policy.backup.maxIntervalHours is not a positive number of hours");
            Check(policy.Backup?.MinKeep is null or > 0, "policy.backup.minKeep is not a positive number");
            foreach (var provider in policy.Providers?.Allowed ?? [])
            {
                Check(_IsStarOrList(provider.Models), $"policy.providers: the models of {provider.Id} are not \"*\" or a list");
            }
        }

        if (manifest.Backup is { } backup)
        {
            Check(backup.Target is null || Origins.Contains(backup.Target.By), "backup.target has an unknown origin");
            Check(backup.Schedule is null || Origins.Contains(backup.Schedule.By), "backup.schedule has an unknown origin");
            Check(backup.RestoreFrom is null or "latest", "backup.restoreFrom is not \"latest\"");
        }

        var allowedPlugins = manifest.Plugins?.Allowed is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(id => id.GetString()).ToList() : null;
        foreach (var connection in manifest.Connections ?? [])
        {
            var notAllowed = forbidden.Contains(connection.Plugin) || (allowedPlugins is not null && !allowedPlugins.Contains(connection.Plugin));
            Check(!(connection.Required && notAllowed), $"connection {connection.Id} is required, but plugin {connection.Plugin} is not allowed");
        }

        return manifest;
    }

    private static ProvisionProfile _Profile(ProvisionProfile profile, int index, Action<bool, string> check, List<ProvisionNote> skipped)
    {
        check(Modes.Contains(profile.Mode) && Origins.Contains(profile.By), $"profile {profile.Id} has an unknown mode or origin");
        check(_IsKnownOrAbsent(profile.Defaults?.PermissionMode, PermissionModes), $"profile {profile.Id} has an unknown permission mode");
        check(_IsKnownOrAbsent(profile.Delegation?.PermissionCeiling, PermissionModes), $"profile {profile.Id} has an unknown delegation ceiling");

        var environment = profile.Environment?.Select((variable, variableIndex) =>
        {
            if (variable is not { Secret: true, Value: not null })
            {
                return variable;
            }

            skipped.Add(new($"profiles.items[{index}].environment[{variableIndex}].value", SkippedNotPortable, profile.By, "a secret's value is asked for on the machine"));
            return variable with { Value = null };
        }).ToList();

        return profile with
        {
            Mode = profile is { Mode: LockedMode, By: "profile" } ? DefaultMode : profile.Mode,
            Environment = environment,
        };
    }

    private static bool _IsKnownOrAbsent(string? value, string[] known) => value is null || known.Contains(value);

    private static bool _IsStarOrList(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() == "*"
        : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);

    private static string _Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    // §3.9: the core never reads a connection's config; it goes on to the plugin as it came.
    private static bool _IsOpaque(string path) => ConnectionConfig().IsMatch(path);

    private static bool _IsEnforced(string path) => path == "policy" || path.StartsWith("policy.", StringComparison.Ordinal)
        || path == "connections" || path.StartsWith("connections[", StringComparison.Ordinal);

    [GeneratedRegex(@"^connections\[\d+\]\.config$")]
    private static partial Regex ConnectionConfig();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\|/[^/\s]|~[\\/])")]
    private static partial Regex LocalPath();
}
