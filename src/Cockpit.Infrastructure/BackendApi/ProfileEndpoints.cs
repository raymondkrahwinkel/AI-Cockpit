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

    private static readonly string[] CredentialFields = ["configJson"];

    private static readonly HashSet<string> LoaderVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "ENV", "BASH_ENV", "SHELLOPTS", "BASHOPTS", "PS4", "PROMPT_COMMAND", "IFS", "GCONV_PATH", "LOCPATH", "HOSTALIASES",
        "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PERL5LIB", "PERL5OPT", "PERLLIB", "RUBYOPT", "RUBYLIB",
    };

    private static readonly string[] LoaderPrefixes = ["LD_", "DYLD_", "BASH_FUNC_"];

    // A field this API does not know is refused rather than dropped, so a misspelt one does not read as saved.
    private static readonly JsonSerializerOptions Strict = new(ConnectKeyEndpoints.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // ponytail: one lock per process, enough for an admin's hand edits.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ISessionProfileStore store() => services.GetRequiredService<ISessionProfileStore>();
        IReadOnlyList<ProfileLoginHealth> health() => services.GetService<IProfileLoginHealth>()?.Current ?? [];

        api.MapGet("/profiles", async (CancellationToken cancellationToken) =>
        {
            var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
            var signIns = health();
            return Results.Json(new { profiles = profiles.Select(profile => ToWire(profile, signIns)) }, ConnectKeyEndpoints.Json);
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
                    .Where(profile => caller.MayStartBypass || !UnsupervisedProfile.SkipsApprovals(profile.Defaults))
                    .Select(profile => new { label = profile.Label, provider = SessionsEndpoints.ProviderId(profile) }),
            });
        }).RequireOperate();

        api.MapPost("/profiles", (HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteNewProfile>(request, cancellationToken, (body, profiles) =>
            {
                if (string.IsNullOrWhiteSpace(body.Label) || services.GetService<IPluginProviderRegistry>()?.Resolve(body.Provider) is null)
                {
                    return (null, _Invalid("A new profile needs a label and the id of one of the server's provider plugins."));
                }

                if (_IndexOf(profiles, body.Label.Trim()) >= 0)
                {
                    return (null, BackendApiRoutes.Error(StatusCodes.Status409Conflict, "profile_exists", "The server already has a profile with that label."));
                }

                var (made, refusal) = _Apply(new SessionProfile(body.Label.Trim(), new PluginProviderConfig(body.Provider, "{}")), body.Settings ?? new RemoteProfilePatch());
                if (made is null)
                {
                    return (null, refusal);
                }

                return ([.. profiles, made], Results.Json(ToWire(made, health()), ConnectKeyEndpoints.Json, statusCode: StatusCodes.Status201Created));
            })).RequireAdmin().Audited("create_profile", services);

        api.MapPatch("/profiles/{label}", (string label, HttpRequest request, CancellationToken cancellationToken) =>
            _ChangeAsync<RemoteProfilePatch>(request, cancellationToken, (patch, profiles) =>
            {
                var index = _IndexOf(profiles, label);
                if (index < 0)
                {
                    return (null, _NoProfile());
                }

                var (changed, refusal) = _Apply(new SessionProfile(profiles[index].Label, profiles[index].ProviderConfig is LmStudioConfig lm ? lm with { ApiKey = null } : profiles[index].ProviderConfig) { EnvironmentVariables = [.. (profiles[index].EnvironmentVariables ?? []).Select(variable => variable with { Value = variable.IsSecret ? "" : variable.Value })] }, patch);
                if (changed is null)
                {
                    return (null, refusal);
                }

                List<SessionProfile> next = [.. profiles];
                next[index] = changed;
                return (next, Results.Json(ToWire(changed, health()), ConnectKeyEndpoints.Json));
            })).RequireOperate().Audited("update_profile", services);

        api.MapDelete("/profiles/{label}", async (string label, CancellationToken cancellationToken) =>
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var profiles = await store().LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = _IndexOf(profiles, label);
                if (index < 0)
                {
                    return _NoProfile();
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
            JsonNode? node;
            try
            {
                node = await JsonNode.ParseAsync(request.Body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                node = null;
            }

            if (node is not JsonObject)
            {
                return _Invalid("The body must be a JSON object.");
            }

            if (Refusal(node) is { } refused)
            {
                return refused;
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

            if (body is null)
            {
                return _Invalid("A profile names only label, provider, settings, model, permissionMode, mcpServers, environment and delegation.");
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

    // A credential, a program path or a loader variable anywhere in the request, before any of it is read.
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
                && name.TryGetValue<string>(out var text) && IsLoaderVariable(text)))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "environment_refused", EnvironmentRefusal);
        }

        return body.Select(property => Refusal(property.Value)).FirstOrDefault(refusal => refusal is not null);
    }

    internal static bool IsLoaderVariable(string key) =>
        LoaderVariables.Contains(key.Trim());

    internal static RemoteProfile ToWire(SessionProfile profile, IReadOnlyList<ProfileLoginHealth> health) => new(
        profile.Label,
        SessionsEndpoints.ProviderId(profile),
        ProfileModel.Of(profile) ?? _Option(profile, WellKnownPluginSessionOptions.Model),
        _Option(profile, WellKnownPluginSessionOptions.PermissionMode),
        profile.EnabledMcpServerNames,
        profile.Defaults?.OptionDefaults,
        profile.DelegationPolicy,
        [.. (profile.EnvironmentVariables ?? []).Select(variable => new RemoteProfileVariable(variable.Key, variable.Value, variable.IsSecret))],
        profile.ProviderConfig is LmStudioConfig { ApiKey.Length: > 0 },
        profile.ProviderConfig is PluginProviderConfig,
        health.FirstOrDefault(entry => string.Equals(entry.Profile, profile.Label, StringComparison.Ordinal))?.SignIn);

    // Only what the patch names; the provider config, and with it an API key or a plugin's own config, is never rebuilt.
    private static (SessionProfile? Profile, IResult Refusal) _Apply(SessionProfile profile, RemoteProfilePatch patch)
    {
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
            var secrets = (profile.EnvironmentVariables ?? []).Where(variable => variable.IsSecret).ToList();
            if (plain.Any(variable => !ProfileEnvironmentVariable.IsValidKey(variable.Key)))
            {
                return (null, _Invalid("An environment variable's name is letters, digits and underscores, not starting with a digit."));
            }

            if (plain.Any(variable => secrets.Any(secret => string.Equals(secret.Key, variable.Key, StringComparison.OrdinalIgnoreCase))))
            {
                return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "credential_refused", CredentialRefusal));
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

    private static string? _Option(SessionProfile profile, string key) =>
        profile.Defaults?.OptionDefaults?.GetValueOrDefault(key);

    // As the spawn path compares labels (AC-1386), so "Foo" and "foo" are one profile here too.
    private static int _IndexOf(IReadOnlyList<SessionProfile> profiles, string label) =>
        profiles.ToList().FindIndex(profile => string.Equals(profile.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IResult _Invalid(string description) =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", description);

    // Never the label it was given, which a mistyped path could have put a secret in.
    private static IResult _NoProfile() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_profile", "The server has no profile with that label.");

    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A profile route ran without a connect-key caller.");
}
