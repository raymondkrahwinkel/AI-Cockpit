using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
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

    private static readonly string[] CredentialFields = ["apiKey", "configJson"];

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
            return Results.Json(new { profiles = profiles.Select(profile => ToWire(profile, signIns, options(profile))) }, ConnectKeyEndpoints.Json);
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

        api.MapPost("/profiles", (HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteNewProfile>(request, cancellationToken, (body, profiles) =>
            {
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

                return ([.. profiles, made], Results.Json(ToWire(made, health(), options(made)), ConnectKeyEndpoints.Json, statusCode: StatusCodes.Status201Created));
            })).RequireAdmin().Audited("create_profile", services);

        api.MapPatch("/profiles/{label}", (string label, HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteProfilePatch>(request, cancellationToken, (patch, profiles) =>
            {
                var index = IndexOf(profiles, label);
                if (index < 0)
                {
                    return (null, NoProfile());
                }

                var (changed, refusal) = Apply(profiles[index], patch, declared(profiles[index]));
                if (changed is null)
                {
                    return (null, refusal);
                }

                List<SessionProfile> next = [.. profiles];
                next[index] = changed;
                return (next, Results.Json(ToWire(changed, health(), options(changed)), ConnectKeyEndpoints.Json));
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
        async Task<IResult> _ChangeAsync<T>(HttpRequest request, CancellationToken cancellationToken, Func<T, IReadOnlyList<SessionProfile>, (IReadOnlyList<SessionProfile>? Next, IResult Answer)> change)
            where T : class
        {
            var (body, refused) = await ReadBodyAsync<T>(request, "A profile names only label, provider, settings, model, permissionMode, mcpServers, environment and delegation.", cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return refused;
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (next, answer) = change(body, await store().LoadAsync(cancellationToken).ConfigureAwait(false));
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
    }

    // The body as T, or why not: a credential, a program path or a loader variable is refused before any of it is read,
    // and a field this API does not know answers `unknownFields`. AC-1475 reads its patch through here too.
    internal static async Task<(T? Body, IResult Refused)> ReadBodyAsync<T>(HttpRequest request, string unknownFields, CancellationToken cancellationToken)
        where T : class
    {
        // A key given twice in two casings throws on first read, as ArgumentException; neither names a value.
        JsonNode? node;
        IResult? refused;
        try
        {
            node = await JsonNode.ParseAsync(request.Body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
            refused = Refusal(node);
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

    internal static IReadOnlyList<ProfileLoginHealth> Health(IServiceProvider services) => services.GetService<IProfileLoginHealth>()?.Current ?? [];

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

    internal static bool IsLoaderVariable(string key) =>
        LoaderVariables.Contains(key.Trim()) || LoaderPrefixes.Any(prefix => key.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static bool IsCredentialName(string key) =>
        CredentialWords.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    // Kept by the server on every change and never shown: a secret, or a plain variable named like a credential.
    private static bool _IsHidden(ProfileEnvironmentVariable variable) => variable.IsSecret || IsCredentialName(variable.Key);

    // Only the option keys the profile's provider declares cross, as list_profiles reports them (AC-649); a stored
    // key it does not declare may hold anything, a plugin's credential included.
    internal static RemoteProfile ToWire(SessionProfile profile, IReadOnlyList<ProfileLoginHealth> health, IReadOnlyList<PluginSessionOptionDescriptor> declaredOptions)
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
            profile.ProviderConfig is LmStudioConfig { ApiKey.Length: > 0 },
            profile.ProviderConfig is PluginProviderConfig,
            health.FirstOrDefault(entry => string.Equals(entry.Profile, profile.Label, StringComparison.Ordinal))?.SignIn,
            known.Count > 0 ? known : null);
    }

    // Only what the patch names; the provider config, and with it an API key or a plugin's own config, is never rebuilt.
    internal static (SessionProfile? Profile, IResult Refusal) Apply(SessionProfile profile, RemoteProfilePatch patch, IReadOnlySet<string> declared)
    {
        var local = profile.ProviderConfig is OllamaConfig or LmStudioConfig;
        if ((patch.Model is not null && !local && !declared.Contains(WellKnownPluginSessionOptions.Model))
            || (patch.PermissionMode is not null && !declared.Contains(WellKnownPluginSessionOptions.PermissionMode)))
        {
            return (null, _Invalid("This profile's provider does not take that option."));
        }

        var changed = profile;
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
