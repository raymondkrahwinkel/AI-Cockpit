using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1473 (F5.6b3): the server's profiles over its admin API. ISessionProfileStore stays the one store: a change
// loads the whole list here, on the server, alters the fields it names and saves it, so a secret the client never
// saw is still in what is saved. No answer, refusal or audit line repeats what a request carried.
internal static class ProfileEndpoints
{
    internal const string CredentialRefusal = "Provider credentials never cross this connection. Mount it as a container secret, or sign the profile in on the server.";

    // AC-1464 (S6b): the server image runs sessions through a wrapper shell as the server's own user; a program path or
    // a loader or interpreter variable from a profile would run there before sudo drops to the agent user.
    private const string ExecutableRefusal = "A profile cannot name a program over this connection; the server runs its own provider CLIs.";

    private const string EnvironmentRefusal = "That environment variable changes how the server loads programs, so a profile cannot set it over this connection.";

    // AC-1534: what a set secret in a plugin profile's config reads as over this connection; sent back, it keeps the key.
    internal const string KeptSecret = "(set on the server)";

    private const string KeyAgainRefusal = "Enter the key again: this change sends it to a different address.";

    // AC-1534: a plugin profile's `configJson` may carry its key to the server (Raymond, 2026-10-13: the server's state
    // holds it in the clear, as it holds the rest); it is write-only, so no answer, audit line or log names it.
    private static readonly string[] CredentialFields = ["apiKey"];

    // The fields of a plugin's config whose change sends a key the server keeps somewhere else (AC-1484's host pin).
    private static readonly string[] AddressWords = ["url", "uri", "host", "endpoint", "address", "origin"];

    private static SecretFields Secrets => SecretKeyHolder.Shared.Fields;

    // AC-1473 (Codex): a variable named like a credential is one, whatever it is marked; B4 lets none cross.
    private static readonly string[] CredentialWords = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH"];

    private static readonly HashSet<string> LoaderVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "ENV", "BASH_ENV", "SHELLOPTS", "BASHOPTS", "PS4", "PROMPT_COMMAND", "IFS", "GCONV_PATH", "LOCPATH", "HOSTALIASES",
        "GLIBC_TUNABLES", "NLSPATH", "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONWARNINGS",
        "PYTHONBREAKPOINT", "PERL5LIB", "PERL5OPT", "PERLLIB", "RUBYOPT", "RUBYLIB", "JAVA_TOOL_OPTIONS", "GIT_SSH", "GIT_SSH_COMMAND",
        "GIT_EXEC_PATH", "GIT_EXTERNAL_DIFF", "GIT_ASKPASS", "SSH_ASKPASS", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "_JAVA_OPTIONS",
    };

    private static readonly string[] LoaderPrefixes = ["LD_", "DYLD_", "BASH_FUNC_", "MALLOC_", "GIT_CONFIG_", "CORECLR_", "COR_PROFILER"];

    // A field this API does not know is refused rather than dropped, so a misspelt one does not read as saved.
    private static readonly JsonSerializerOptions Strict = new(ConnectKeyEndpoints.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static SemaphoreSlim Gate => ProfileEdits.Gate;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ISessionProfileStore store() => services.GetRequiredService<ISessionProfileStore>();
        IReadOnlyList<ProfileLoginHealth> health() => Health(services);
        IReadOnlySet<string> declared(SessionProfile profile) => Declared(services, profile);
        IReadOnlyList<PluginSessionOptionDescriptor> options(SessionProfile profile) => Options(services, profile);

        api.MapGet("/profiles", async (CancellationToken cancellationToken) =>
        {
            var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
            var signIns = health();
            // AC-1534: the providers a new profile can name, also one no profile uses yet.
            var providers = services.GetService<IPluginProviderRegistry>()?.Registrations.Select(registration => registration.ProviderId) ?? [];
            return Results.Json(new { profiles = profiles.Select(profile => ToWire(profile, signIns, options(profile), EditorKeys(services, profile))), providers }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("list_profiles", services);

        // The new-session dialog's list for any key: label and provider of what it may start, nothing more.
        api.MapGet("/profiles/startable", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var pairing = services.GetRequiredService<INodePairingBroker>();
            var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                profiles = profiles
                    .Where(profile => caller.AllowsProfile(profile.Label, pairing) && (caller.MayStartBypass || !UnsupervisedProfile.SkipsApprovals(profile.Defaults)))
                    .Select(profile => new { label = profile.Label, provider = SessionsEndpoints.ProviderId(profile) }),
            });
        }).RequireOperate();

        // AC-1585: what the profile editor offers, read from the server rather than from the desktop that edits.
        api.MapGet("/profiles/providers", () =>
        {
            var registry = services.GetService<IPluginProviderRegistry>();
            var tty = services.GetService<IPluginTtyProviderRegistry>();
            return Results.Json((registry?.Registrations ?? []).Select(registration => new RemoteProviderInfo(
                registration.ProviderId,
                registration.DisplayName,
                registry?.OwnerOf(registration.ProviderId),
                registration.Capabilities.SupportsEnvVars,
                registration.Capabilities.SupportsPermissions,
                tty?.Resolve(registration.ProviderId) is not null,
                [.. registration.Options.Select(option => new RemoteProviderOption(option.Key, option.Label, option.Choices, option.ChoiceLabels))])), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("list_profile_providers", services);

        api.MapGet("/profiles/mcp-servers", async (CancellationToken cancellationToken) =>
            Results.Json(services.GetService<IMcpServerCatalog>() is { } catalog
                ? McpServerRegistryFilter.OfferedToOperator(await catalog.GetServersAsync(cancellationToken).ConfigureAwait(false)).Select(server => server.Name)
                : [], ConnectKeyEndpoints.Json)).RequireAdmin().Audited("list_profile_mcp_servers", services);

        api.MapGet("/profiles/mcp-servers/{name}/estimate", async (string name, bool? refresh, CancellationToken cancellationToken) =>
            Results.Json(services.GetService<IMcpToolTokenEstimator>() is { } estimator
                ? await estimator.EstimateAsync(name, refresh ?? false, cancellationToken).ConfigureAwait(false)
                : McpServerToolEstimate.Unavailable(name), ConnectKeyEndpoints.Json)).RequireAdmin().Audited("estimate_profile_mcp_server", services);

        // A key that reads KeptSecret is the one the named profile holds, and only for the address it holds it for.
        api.MapPost("/profiles/models", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var (body, refused) = await ReadBodyAsync<RemoteModelsRequest>(request, "The body names only baseUrl, apiKey and label.", cancellationToken, root => root).ConfigureAwait(false);
            if (body is null)
            {
                return refused;
            }

            if (services.GetService<IModelCatalog>() is not { } catalog)
            {
                return Results.Json(Array.Empty<string>(), ConnectKeyEndpoints.Json);
            }

            var key = body.ApiKey;
            if (key == KeptSecret)
            {
                var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = body.Label is { } label ? IndexOf(profiles, label) : -1;
                key = index >= 0 && profiles[index].ProviderConfig is LmStudioConfig { ApiKey.Length: > 0 } held && held.BaseUrl == body.BaseUrl
                    ? held.ApiKey
                    : null;
            }

            return Results.Json(await catalog.ListModelsAsync(body.BaseUrl, string.IsNullOrWhiteSpace(key) ? null : key, cancellationToken).ConfigureAwait(false), ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("list_profile_models", services);

        api.MapPost("/profiles", (HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteNewProfile>(request, cancellationToken, root => root["settings"] as JsonObject, async (body, profiles) =>
            {
                if (body.Settings?.Profile is { } whole)
                {
                    return await _WholeAsync(profiles, -1, whole, body.Provider, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(body.Label) || string.IsNullOrWhiteSpace(body.Provider)
                    || services.GetService<IPluginProviderRegistry>()?.Resolve(body.Provider) is null)
                {
                    return (null, _Invalid("A new profile needs a label and the id of one of the server's provider plugins."));
                }

                if (IndexOf(profiles, body.Label.Trim()) >= 0)
                {
                    return (null, BackendApiRoutes.Error(StatusCodes.Status409Conflict, "profile_exists", "The server already has a profile with that label."));
                }

                var fresh = new SessionProfile(body.Label.Trim(), new PluginProviderConfig(body.Provider, "{}"));
                var (made, refusal) = Apply(fresh, body.Settings ?? new RemoteProfilePatch(), declared(fresh));
                if (made is null)
                {
                    return (null, refusal);
                }

                return ([.. profiles, made], Results.Json(ToWire(made, health(), options(made), EditorKeys(services, made)), ConnectKeyEndpoints.Json, statusCode: StatusCodes.Status201Created));
            })).RequireAdmin().Audited("create_profile", services);

        api.MapPatch("/profiles/{label}", (string label, HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteProfilePatch>(request, cancellationToken, root => root, async (patch, profiles) =>
            {
                var index = IndexOf(profiles, label);
                if (index < 0)
                {
                    return (null, NoProfile());
                }

                if (patch.Profile is { } whole)
                {
                    return await _WholeAsync(profiles, index, whole, null, cancellationToken).ConfigureAwait(false);
                }

                var (changed, refusal) = Apply(profiles[index], patch, declared(profiles[index]));
                if (changed is null)
                {
                    return (null, refusal);
                }

                List<SessionProfile> next = [.. profiles];
                next[index] = changed;
                return (next, Results.Json(ToWire(changed, health(), options(changed), EditorKeys(services, changed)), ConnectKeyEndpoints.Json));
            })).RequireAdmin().Audited("update_profile", services);

        api.MapDelete("/profiles/{label}", async (string label, CancellationToken cancellationToken) =>
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = IndexOf(profiles, label);
                if (index < 0)
                {
                    return NoProfile();
                }

                // An empty list reads back as the providers' auto-detected profiles, so the last one would come back.
                if (profiles.Count == 1)
                {
                    return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "last_profile", "The server keeps at least one profile.");
                }

                await store().SaveAsync([.. profiles.Where((_, at) => at != index)], cancellationToken).ConfigureAwait(false);
                return Results.Json(new { ok = true, deleted = true });
            }
            finally
            {
                Gate.Release();
            }
        }).RequireAdmin().Audited("delete_profile", services);

        // Read, refuse, then load, change and save under the one lock.
        async Task<IResult> _ChangeAsync<T>(HttpRequest request, CancellationToken cancellationToken, Func<JsonObject, JsonObject?> wholeProfile, Func<T, IReadOnlyList<SessionProfile>, Task<(IReadOnlyList<SessionProfile>? Next, IResult Answer)>> change)
            where T : class
        {
            var (body, refused) = await ReadBodyAsync<T>(request, "A profile names only label, provider, settings, model, permissionMode, mcpServers, environment, delegation, configJson and profile.", cancellationToken, wholeProfile).ConfigureAwait(false);
            if (body is null)
            {
                return refused;
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (next, answer) = await change(body, await store().LoadAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                if (next is not null)
                {
                    await store().SaveAsync(next, cancellationToken).ConfigureAwait(false);
                }

                return answer;
            }
            finally
            {
                Gate.Release();
            }
        }

        // AC-1585: a whole profile made (`index` -1) or put in place of the one at `index`, which may rename it.
        async Task<(IReadOnlyList<SessionProfile>? Next, IResult Answer)> _WholeAsync(IReadOnlyList<SessionProfile> profiles, int index, JsonElement json, string? provider, CancellationToken cancellationToken)
        {
            if (RemoteProfileWire.FromJson(json) is not { } incoming)
            {
                return (null, _Invalid("profile must hold a profile."));
            }

            if (provider is not null && !string.Equals(provider, SessionsEndpoints.ProviderId(incoming), StringComparison.Ordinal))
            {
                return (null, _Invalid("The profile names a different provider than the request."));
            }

            var choices = incoming.ProviderConfig is PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId }
                ? await _ClaudeChoicesAsync(services, cancellationToken).ConfigureAwait(false)
                : null;
            var (merged, refusal) = MergeWhole(index < 0 ? null : profiles[index], incoming, services, choices);
            if (merged is null)
            {
                return (null, refusal);
            }

            var clash = IndexOf(profiles, merged.Label);
            if (clash >= 0 && clash != index)
            {
                return (null, BackendApiRoutes.Error(StatusCodes.Status409Conflict, "profile_exists", "The server already has a profile with that label."));
            }

            List<SessionProfile> next = [.. profiles];
            if (index < 0)
            {
                next.Add(merged);
            }
            else
            {
                next[index] = merged;
            }

            return (next, Results.Json(ToWire(merged, health(), options(merged), EditorKeys(services, merged)), ConnectKeyEndpoints.Json, statusCode: index < 0 ? StatusCodes.Status201Created : StatusCodes.Status200OK));
        }
    }

    private const string ConfigDirRefusal = "Pick one of the server's own Claude folders; a profile cannot name another folder over this connection.";

    private const string WorkingDirectoryRefusal = "The server has no such folder for the default working directory.";

    // AC-1585: `incoming` as the editor sent it, held to what a change field by field is held to. Each secret that reads
    // KeptSecret is put back from `held`, and only where its address did not change; a new profile has none to put back.
    internal static (SessionProfile? Profile, IResult Refusal) MergeWhole(SessionProfile? held, SessionProfile incoming, IServiceProvider services, ClaudeChoices? claude = null)
    {
        var provider = SessionsEndpoints.ProviderId(incoming);
        if (string.IsNullOrWhiteSpace(incoming.Label))
        {
            return (null, _Invalid("A profile needs a label."));
        }

        if (held is not null ? provider != SessionsEndpoints.ProviderId(held)
            : incoming.ProviderConfig is not (OllamaConfig or LmStudioConfig) && services.GetService<IPluginProviderRegistry>()?.Resolve(provider) is null)
        {
            return (null, _Invalid(held is null ? "A new profile needs one of the server's providers." : "A profile's provider is fixed once it is made."));
        }

        var (config, configRefusal) = _MergeProviderConfig(held, incoming.ProviderConfig, claude ?? ClaudeChoices.None);
        if (config is null)
        {
            return (null, configRefusal);
        }

        var (variables, variableRefusal) = _MergeEnvironment(held?.EnvironmentVariables ?? [], incoming.EnvironmentVariables ?? []);
        if (variables is null)
        {
            return (null, variableRefusal);
        }

        if (incoming.DefaultWorkingDirectory is { Length: > 0 } folder && folder != held?.DefaultWorkingDirectory && !Directory.Exists(folder))
        {
            return (null, _Invalid(WorkingDirectoryRefusal));
        }

        // An option the provider does not declare never crossed, so the server's own value stays.
        var declared = EditorKeys(services, incoming);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in held?.Defaults?.OptionDefaults?.Where(option => !declared.Contains(option.Key)) ?? [])
        {
            options[key] = value;
        }

        foreach (var (key, value) in incoming.Defaults?.OptionDefaults?.Where(option => declared.Contains(option.Key)) ?? [])
        {
            options[key] = value;
        }

        return (incoming with
        {
            Label = incoming.Label.Trim(),
            ProviderConfig = config,
            Defaults = (incoming.Defaults ?? (options.Count > 0 ? new ProfileDefaults("", "", "") : null)) is { } defaults
                ? defaults with { OptionDefaults = options.Count > 0 ? options : null }
                : null,
            EnvironmentVariables = variables.Count > 0 ? variables : null,
        }, Results.Empty);
    }

    private static (ProviderConfig? Config, IResult Refusal) _MergeProviderConfig(SessionProfile? held, ProviderConfig incoming, ClaudeChoices claude)
    {
        switch (incoming)
        {
            case OllamaConfig:
                return (incoming, Results.Empty);
            case LmStudioConfig { ApiKey: KeptSecret } lmStudio when held?.ProviderConfig is LmStudioConfig { ApiKey.Length: > 0 } stored:
                return stored.BaseUrl == lmStudio.BaseUrl ? (lmStudio with { ApiKey = stored.ApiKey }, Results.Empty) : (null, KeyAgain);
            case LmStudioConfig { ApiKey: KeptSecret } lmStudio:
                return (lmStudio with { ApiKey = null }, Results.Empty);
            case LmStudioConfig:
                return (incoming, Results.Empty);
            case PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId } claudeConfig:
                return _ClaudeRefusal(held?.Claude, ClaudePluginProfile.ReadClaudeConfig(claudeConfig.ConfigJson), claude) is { } refusal ? (null, refusal) : (claudeConfig, Results.Empty);
            case PluginProviderConfig plugin:
                var (merged, mergeRefusal) = MergeConfig(held?.ProviderConfig is PluginProviderConfig heldPlugin ? heldPlugin.ConfigJson : "{}", plugin.ConfigJson);
                return merged is null ? (null, mergeRefusal) : (plugin with { ConfigJson = merged }, Results.Empty);
            default:
                return (null, _Invalid("That provider's settings do not cross this connection."));
        }
    }

    // B3 (AC-1585): the server's own choice, not a path from the desktop. Blank is the server's own resolution; a folder
    // or program is one the provider itself found on the server; what the profile held already passes unchanged.
    private static IResult? _ClaudeRefusal(ClaudeConfig? held, ClaudeConfig next, ClaudeChoices choices)
    {
        if (next.ExecutablePath is { Length: > 0 } program && program != held?.ExecutablePath && !choices.Programs.Contains(program))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "executable_refused", ExecutableRefusal);
        }

        return next.ConfigDir is { Length: > 0 } folder && folder != held?.ConfigDir && !choices.Folders.Contains(folder)
            ? BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "config_dir_refused", ConfigDirRefusal)
            : null;
    }

    // What the Claude provider's own backend part answers its view (`config-choices`), asked in-process. A provider that
    // cannot say offers nothing, so only blank and what a profile already held pass.
    private static async Task<ClaudeChoices> _ClaudeChoicesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<IPluginProviderRegistry>()?.OwnerOf(ClaudePluginProfile.ProviderId) is not { } owner
            || services.GetService<PluginChannelHub>() is not { } channels)
        {
            return ClaudeChoices.None;
        }

        try
        {
            var answer = await channels.InvokeAsync(owner, "config-choices", JsonSerializer.SerializeToElement(new { }), cancellationToken).ConfigureAwait(false);
            return new ClaudeChoices(_Strings(answer, "configDirs", "configDir"), _Strings(answer, "executables", null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ClaudeChoices.None;
        }
    }

    private static HashSet<string> _Strings(JsonElement answer, string list, string? field)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in items.EnumerateArray())
        {
            var value = field is null ? item : item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var inner) ? inner : default;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                values.Add(text);
            }
        }

        return values;
    }

    internal sealed record ClaudeChoices(IReadOnlySet<string> Folders, IReadOnlySet<string> Programs)
    {
        public static ClaudeChoices None { get; } = new(new HashSet<string>(), new HashSet<string>());
    }

    // A value that reads KeptSecret is the one the server holds under that name; a loader variable passes only unchanged.
    private static (List<ProfileEnvironmentVariable>? Variables, IResult Refusal) _MergeEnvironment(IReadOnlyList<ProfileEnvironmentVariable> held, IReadOnlyList<ProfileEnvironmentVariable> incoming)
    {
        List<ProfileEnvironmentVariable> merged = [];
        foreach (var variable in incoming)
        {
            if (!ProfileEnvironmentVariable.IsValidKey(variable.Key))
            {
                return (null, _Invalid("An environment variable's name is letters, digits and underscores, not starting with a digit."));
            }

            var stored = held.FirstOrDefault(entry => string.Equals(entry.Key, variable.Key, StringComparison.OrdinalIgnoreCase));
            if (variable.Value == KeptSecret)
            {
                if (stored is null || !_IsHidden(stored))
                {
                    return (null, _Invalid("Enter that variable's value again: the server holds none to keep."));
                }

                merged.Add(stored);
            }
            else if (IsLoaderVariable(variable.Key) && stored?.Value != variable.Value)
            {
                return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "environment_refused", EnvironmentRefusal));
            }
            else
            {
                merged.Add(variable);
            }
        }

        return (merged, Results.Empty);
    }

    // The body as T, or why not: a credential, a program path or a loader variable is refused before any of it is read,
    // and a field this API does not know answers `unknownFields`. AC-1475 reads its patch through here too.
    // AC-1585: `wholeProfile` names where a create or change carries a whole profile, whose secrets cross write-only;
    // MergeWhole holds that part to the rules field by field, so the scan here skips it.
    internal static async Task<(T? Body, IResult Refused)> ReadBodyAsync<T>(HttpRequest request, string unknownFields, CancellationToken cancellationToken, Func<JsonObject, JsonObject?>? wholeProfile = null)
        where T : class
    {
        // A key given twice in two casings throws on first read, as ArgumentException; neither names a value.
        JsonNode? node;
        IResult? refused;
        try
        {
            node = await JsonNode.ParseAsync(request.Body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
            refused = Refusal(wholeProfile is not null && node is JsonObject root ? _Without(root, wholeProfile) : node);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            node = null;
            refused = null;
        }

        if (node is not JsonObject)
        {
            return (null, _Invalid("The body must be a JSON object."));
        }

        if (refused is not null)
        {
            return (null, refused);
        }

        T? body;
        try
        {
            body = node.Deserialize<T>(Strict);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body is null ? (null, _Invalid(unknownFields)) : (body, Results.Empty);
    }

    private static JsonObject _Without(JsonObject root, Func<JsonObject, JsonObject?> holder)
    {
        var copy = (JsonObject)root.DeepClone();
        holder(copy)?.Remove("profile");
        return copy;
    }

    internal static IReadOnlyList<ProfileLoginHealth> Health(IServiceProvider services) => services.GetService<IProfileLoginHealth>()?.Current ?? [];

    // AC-1585: what the profile editor reads and writes: the declared options plus the start options it offers.
    internal static IReadOnlySet<string> EditorKeys(IServiceProvider services, SessionProfile profile) =>
        Declared(services, profile)
            .Concat(services.GetService<IPluginProviderRegistry>()?.Resolve(SessionsEndpoints.ProviderId(profile))?.Options.Select(option => option.Key) ?? [])
            .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string> Declared(IServiceProvider services, SessionProfile profile) =>
        Options(services, profile).Select(option => option.Key).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<PluginSessionOptionDescriptor> Options(IServiceProvider services, SessionProfile profile) =>
        services.GetService<IPluginProviderRegistry>()?.Resolve(SessionsEndpoints.ProviderId(profile))?.Capabilities.DeclaredOptions ?? [];

    // A credential or a program path anywhere in the request, before any of it is read.
    internal static IResult? Refusal(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.Select(Refusal).FirstOrDefault(refusal => refusal is not null);
        }

        if (node is not JsonObject body)
        {
            return null;
        }

        var isSecret = body.TryGetPropertyValue("isSecret", out var secret) && secret is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        if (isSecret || CredentialFields.Any(field => body.ContainsKey(field)))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "credential_refused", CredentialRefusal);
        }

        if (body.ContainsKey("executablePath"))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "executable_refused", ExecutableRefusal);
        }

        if (body.TryGetPropertyValue("environment", out var environment) && environment is JsonArray variables
            && variables.Any(variable => variable is JsonObject entry && entry.TryGetPropertyValue("key", out var key) && key is JsonValue name
                && name.TryGetValue<string>(out var text) && IsCredentialName(text)))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "credential_refused", CredentialRefusal);
        }

        return body.Select(property => Refusal(property.Value)).FirstOrDefault(refusal => refusal is not null);
    }

    // AC-1475 (Codex): what a profile copied into the assistant's slot may not hold, since the assistant's start would run
    // it: a program path (stored as a plugin's `executablePath`), or a loader variable. In the words a PATCH gets.
    internal static IResult? CopyRefusal(SessionProfile profile) =>
        profile.ProviderConfig is ClaudeConfig { ExecutablePath.Length: > 0 }
            || (profile.ProviderConfig is PluginProviderConfig plugin && ClaudePluginProfile.ReadClaudeConfig(plugin.ConfigJson).ExecutablePath is not null)
            ? BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "executable_refused", ExecutableRefusal)
            : (profile.EnvironmentVariables ?? []).Any(variable => IsLoaderVariable(variable.Key))
                ? BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "environment_refused", EnvironmentRefusal)
                : null;

    internal static bool IsLoaderVariable(string key) =>
        LoaderVariables.Contains(key.Trim()) || LoaderPrefixes.Any(prefix => key.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static bool IsCredentialName(string key) =>
        CredentialWords.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    // Kept by the server on every change and never shown: a secret, or a plain variable named like a credential.
    private static bool _IsHidden(ProfileEnvironmentVariable variable) => variable.IsSecret || IsCredentialName(variable.Key);

    // Only the option keys the profile's provider declares cross, as list_profiles reports them (AC-649); a stored
    // key it does not declare may hold anything, a plugin's credential included.
    internal static RemoteProfile ToWire(SessionProfile profile, IReadOnlyList<ProfileLoginHealth> health, IReadOnlyList<PluginSessionOptionDescriptor> declaredOptions, IReadOnlySet<string>? editorKeys = null)
    {
        var declared = declaredOptions.Select(option => option.Key).ToHashSet(StringComparer.Ordinal);
        var known = declaredOptions.Where(option => option.KnownValues is { Count: > 0 })
            .ToDictionary(option => option.Key, IReadOnlyList<RemoteOptionValue> (option) => [.. (option.KnownValues ?? []).Select(value => new RemoteOptionValue(value.Value, value.Label))], StringComparer.Ordinal);
        var options = profile.Defaults?.OptionDefaults?.Where(option => declared.Contains(option.Key)).ToDictionary(option => option.Key, option => option.Value);
        return new(
            profile.Label,
            SessionsEndpoints.ProviderId(profile),
            ProfileModel.Of(profile) ?? options?.GetValueOrDefault(WellKnownPluginSessionOptions.Model),
            options?.GetValueOrDefault(WellKnownPluginSessionOptions.PermissionMode),
            profile.EnabledMcpServerNames,
            options is { Count: > 0 } ? options : null,
            profile.DelegationPolicy,
            [.. (profile.EnvironmentVariables ?? []).Select(variable => _IsHidden(variable)
                ? new RemoteProfileVariable(variable.Key, null, true)
                : new RemoteProfileVariable(variable.Key, variable.Value))],
            profile.ProviderConfig is LmStudioConfig { ApiKey.Length: > 0 } || (profile.ProviderConfig is PluginProviderConfig held && _HoldsSecret(_ParseObject(held.ConfigJson))),
            profile.ProviderConfig is PluginProviderConfig,
            health.FirstOrDefault(entry => string.Equals(entry.Profile, profile.Label, StringComparison.Ordinal))?.SignIn,
            known.Count > 0 ? known : null,
            _TakesConfig(profile) is { } plugin ? Redacted(plugin.ConfigJson) : null,
            RemoteProfileWire.ToJson(Masked(profile, editorKeys ?? declared)));
    }

    // AC-1585: the profile as the editor gets it. Each secret reads KeptSecret, and of the option defaults only the
    // declared ones cross, as above.
    internal static SessionProfile Masked(SessionProfile profile, IReadOnlySet<string> declared) => profile with
    {
        ProviderConfig = profile.ProviderConfig switch
        {
            LmStudioConfig { ApiKey.Length: > 0 } lmStudio => lmStudio with { ApiKey = KeptSecret },
            PluginProviderConfig plugin => plugin with { ConfigJson = Redacted(plugin.ConfigJson) },
            var other => other,
        },
        Defaults = profile.Defaults is { } defaults
            ? defaults with { OptionDefaults = _Declared(defaults.OptionDefaults, declared) }
            : null,
        EnvironmentVariables = profile.EnvironmentVariables?.Select(variable => _IsHidden(variable) ? variable with { Value = KeptSecret } : variable).ToList(),
    };

    private static IReadOnlyDictionary<string, string>? _Declared(IReadOnlyDictionary<string, string>? options, IReadOnlySet<string> declared) =>
        options?.Where(option => declared.Contains(option.Key)).ToDictionary(option => option.Key, option => option.Value) is { Count: > 0 } kept ? kept : null;

    // AC-1534: the config with every secret string that is set read as KeptSecret, so the provider's own view opens
    // with its key field filled (its check passes) and says only that one is set.
    internal static string Redacted(string configJson)
    {
        var config = _ParseObject(configJson) ?? [];
        Mask(config, Secrets.IsSecret);
        return config.ToJsonString();
    }

    // A secret that reads KeptSecret, or is left out, stays as stored; empty clears it. A kept one never follows a changed
    // address, or an admin key could send it to a host of its own.
    internal static (string? Config, IResult Refusal) MergeConfig(string stored, string incoming)
    {
        if (_ParseObject(incoming) is not { } next)
        {
            return (null, _Invalid("configJson must hold a JSON object."));
        }

        if (_FilledNames(next).Any(name => string.Equals(name, "executablePath", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "executable_refused", ExecutableRefusal));
        }

        return KeepSecrets(_ParseObject(stored) ?? [], next, Secrets.IsSecret)
            ? (next.ToJsonString(), Results.Empty)
            : (null, KeyAgain);
    }

    internal static IResult KeyAgain => BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "key_required", KeyAgainRefusal);

    // Puts each secret `next` reads as KeptSecret (or leaves out) back from `held`, at any depth (AC-1571: a plugin's
    // storage nests them). False when an object holding a kept one, itself or below it, changed its address.
    // ponytail: array items pair up by position, so a reordered list asks for its keys again; match by id if that grates.
    internal static bool KeepSecrets(JsonNode? held, JsonNode? next, Func<string, bool> isSecret) => _Keep(held, next, isSecret, out _);

    private static bool _Keep(JsonNode? held, JsonNode? next, Func<string, bool> isSecret, out bool kept)
    {
        kept = false;
        if (held is JsonArray heldItems && next is JsonArray nextItems)
        {
            for (var index = 0; index < nextItems.Count; index++)
            {
                if (index < heldItems.Count && _IsKept(nextItems[index]) && _HoldsSecretAsText(heldItems[index], isSecret))
                {
                    nextItems[index] = heldItems[index]?.DeepClone();
                    kept = true;
                }
                else if (index >= heldItems.Count || _IsKept(nextItems[index]))
                {
                    _ClearKept(nextItems, index);
                }
                else if (!_Keep(heldItems[index], nextItems[index], isSecret, out var below))
                {
                    return false;
                }
                else
                {
                    kept |= below;
                }
            }

            return true;
        }

        if (held is not JsonObject heldConfig || next is not JsonObject nextConfig)
        {
            _ClearKept(next);
            return true;
        }

        foreach (var (name, value) in nextConfig.ToList())
        {
            var stored = heldConfig.TryGetPropertyValue(name, out var heldValue) ? heldValue : null;
            if (_IsKept(value) && _Hides(name, stored, isSecret))
            {
                nextConfig[name] = stored?.DeepClone();
                kept = true;
            }
            else if (_IsKept(value))
            {
                // One that reads KeptSecret with nothing behind it is not a key.
                nextConfig[name] = "";
            }
            else if (!_Keep(stored, value, isSecret, out var below))
            {
                return false;
            }
            else
            {
                kept |= below;
            }
        }

        foreach (var (name, value) in heldConfig.Where(property => !nextConfig.ContainsKey(property.Key)).ToList())
        {
            if (_Hides(name, value, isSecret))
            {
                nextConfig[name] = value?.DeepClone();
                kept = true;
            }
        }

        var addresses = heldConfig.Select(property => property.Key).Concat(nextConfig.Select(property => property.Key))
            .Where(name => AddressWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        return !kept || addresses.All(name => JsonNode.DeepEquals(heldConfig[name], nextConfig[name]));
    }

    private static void _ClearKept(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                _ClearKept(array, index);
            }
        }
        else if (node is JsonObject config)
        {
            foreach (var (name, value) in config.ToList())
            {
                if (_IsKept(value))
                {
                    config[name] = "";
                }
                else
                {
                    _ClearKept(value);
                }
            }
        }
    }

    private static void _ClearKept(JsonArray array, int index)
    {
        if (_IsKept(array[index]))
        {
            array[index] = "";
        }
        else
        {
            _ClearKept(array[index]);
        }
    }

    // Case-insensitive, as the providers read their own config.
    private static JsonObject? _ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool _IsFilled(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var content) && content.Length > 0;

    private static bool _IsKept(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var content) && content == KeptSecret;

    // Every set secret at any depth reads as KeptSecret: whatever a secret name holds but a yes/no, and JSON text that
    // carries one (a plugin storing its config as a string), whole.
    internal static void Mask(JsonNode? node, Func<string, bool> isSecret)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (_HoldsSecretAsText(array[index], isSecret))
                {
                    array[index] = KeptSecret;
                }
                else
                {
                    Mask(array[index], isSecret);
                }
            }
        }
        else if (node is JsonObject config)
        {
            foreach (var (name, value) in config.ToList())
            {
                if (_Hides(name, value, isSecret))
                {
                    config[name] = KeptSecret;
                }
                else
                {
                    Mask(value, isSecret);
                }
            }
        }
    }

    private static bool _Hides(string name, JsonNode? value, Func<string, bool> isSecret) =>
        (isSecret(name) && _IsSet(value)) || _HoldsSecretAsText(value, isSecret);

    // Text, or a list holding text ("tokens": [...]); a number under such a name is a limit ("maxTokens"), not a key.
    private static bool _IsSet(JsonNode? value) => value switch
    {
        JsonValue scalar when scalar.TryGetValue<string>(out var content) => content.Length > 0,
        JsonArray items => items.Any(_IsSet),
        _ => false,
    };

    private static bool _HoldsSecretAsText(JsonNode? value, Func<string, bool> isSecret)
    {
        if (value is not JsonValue text || !text.TryGetValue<string>(out var content) || content.TrimStart() is not ['{' or '[', ..])
        {
            return false;
        }

        try
        {
            return _HoldsSecret(JsonNode.Parse(content), isSecret);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool _HoldsSecret(JsonNode? node) => _HoldsSecret(node, Secrets.IsSecret);

    private static bool _HoldsSecret(JsonNode? node, Func<string, bool> isSecret) => node switch
    {
        JsonArray array => array.Any(item => _HoldsSecretAsText(item, isSecret) || _HoldsSecret(item, isSecret)),
        JsonObject config => config.Any(property => _Hides(property.Key, property.Value, isSecret) || _HoldsSecret(property.Value, isSecret)),
        _ => false,
    };

    private static IEnumerable<string> _FilledNames(JsonNode? node) => node switch
    {
        JsonArray array => array.SelectMany(_FilledNames),
        JsonObject config => config.SelectMany(property => _IsFilled(property.Value) ? [property.Key] : _FilledNames(property.Value)),
        _ => [],
    };

    // The Claude provider's config names its config directory and program on the server (AC-1464), so it stays there.
    private static PluginProviderConfig? _TakesConfig(SessionProfile profile) =>
        profile.ProviderConfig is PluginProviderConfig { ProviderId: not ClaudePluginProfile.ProviderId } plugin ? plugin : null;

    // Only what the patch names; the provider config, and with it an API key or a plugin's own config, is never rebuilt.
    internal static (SessionProfile? Profile, IResult Refusal) Apply(SessionProfile profile, RemoteProfilePatch patch, IReadOnlySet<string> declared)
    {
        // A whole profile goes through MergeWhole; here it would skip the secret rules.
        if (patch.Profile is not null)
        {
            return (null, _Invalid("This route takes a profile field by field."));
        }

        var local = profile.ProviderConfig is OllamaConfig or LmStudioConfig;
        if ((patch.Model is not null && !local && !declared.Contains(WellKnownPluginSessionOptions.Model))
            || (patch.PermissionMode is not null && !declared.Contains(WellKnownPluginSessionOptions.PermissionMode)))
        {
            return (null, _Invalid("This profile's provider does not take that option."));
        }

        var changed = profile;
        if (patch.ConfigJson is { } configJson)
        {
            if (_TakesConfig(profile) is not { } plugin)
            {
                return (null, _Invalid("Only a plugin provider's profile takes a configJson."));
            }

            var (merged, refusal) = MergeConfig(plugin.ConfigJson, configJson);
            if (merged is null)
            {
                return (null, refusal);
            }

            changed = changed with { ProviderConfig = plugin with { ConfigJson = merged } };
        }

        if (patch.Model is { } model)
        {
            changed = changed.ProviderConfig switch
            {
                OllamaConfig ollama => changed with { ProviderConfig = ollama with { Model = model.Trim() } },
                LmStudioConfig lmStudio => changed with { ProviderConfig = lmStudio with { Model = model.Trim() } },
                _ => _WithOption(changed, WellKnownPluginSessionOptions.Model, model),
            };
        }

        if (patch.PermissionMode is { } mode)
        {
            changed = _WithOption(changed, WellKnownPluginSessionOptions.PermissionMode, mode);
        }

        if (patch.McpServers is { } servers)
        {
            changed = changed with { EnabledMcpServerNames = servers.Names };
        }

        if (patch.Delegation is { } delegation)
        {
            changed = changed with { Delegation = delegation };
        }

        if (patch.Environment is { } plain)
        {
            var secrets = (profile.EnvironmentVariables ?? []).Where(_IsHidden).ToList();
            if (plain.Any(variable => !ProfileEnvironmentVariable.IsValidKey(variable.Key)))
            {
                return (null, _Invalid("An environment variable's name is letters, digits and underscores, not starting with a digit."));
            }

            if (plain.Any(variable => secrets.Any(secret => string.Equals(secret.Key, variable.Key, StringComparison.OrdinalIgnoreCase))))
            {
                return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "credential_refused", CredentialRefusal));
            }

            // One the server already holds, set there, passes unchanged, so it does not lock the other variables.
            var stored = profile.EnvironmentVariables ?? [];
            if (plain.Any(variable => IsLoaderVariable(variable.Key) && !stored.Any(held => held.Key == variable.Key && held.Value == (variable.Value ?? ""))))
            {
                return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "environment_refused", EnvironmentRefusal));
            }

            changed = changed with { EnvironmentVariables = [.. secrets, .. plain.Select(variable => new ProfileEnvironmentVariable(variable.Key, variable.Value ?? ""))] };
        }

        return (changed, Results.Empty);
    }

    // An empty value takes the option out, so the provider's own default applies again.
    private static SessionProfile _WithOption(SessionProfile profile, string key, string value)
    {
        var defaults = profile.Defaults ?? new ProfileDefaults("", "", "");
        var options = new Dictionary<string, string>(defaults.OptionDefaults ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            options.Remove(key);
        }
        else
        {
            options[key] = value.Trim();
        }

        return profile with { Defaults = defaults with { OptionDefaults = options.Count > 0 ? options : null } };
    }

    // As the spawn path compares labels (AC-1386), so "Foo" and "foo" are one profile here too.
    internal static int IndexOf(IReadOnlyList<SessionProfile> profiles, string label) =>
        profiles.ToList().FindIndex(profile => string.Equals(profile.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IResult _Invalid(string description) =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", description);

    // Never the label it was given, which a mistyped path could have put a secret in.
    internal static IResult NoProfile() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_profile", "The server has no profile with that label.");

    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A profile route ran without a connect-key caller.");
}

// AC-1585: the profile editor's model list, asked from the server.
internal sealed record RemoteModelsRequest(string BaseUrl, string? ApiKey = null, string? Label = null);
