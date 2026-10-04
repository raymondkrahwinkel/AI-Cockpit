using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1475: the server's own assistant over its admin API. Each change loads what is stored, alters only what it names
// and saves that, so the consent bypass and a secret the client never saw stay put. No MCP tool reaches this
// (AssistantSettingsWritersTests), and no answer or audit line repeats what a request carried.
internal static class AssistantSettingsEndpoints
{
    private const string Route = "/assistant/settings";

    private static SemaphoreSlim Gate => ProfileEdits.Gate;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IAssistantSettingsStore settings() => services.GetRequiredService<IAssistantSettingsStore>();
        IAssistantProfileStore slot() => services.GetRequiredService<IAssistantProfileStore>();
        IAssistantSessionHost host() => services.GetRequiredService<IAssistantSessionHost>();

        api.MapGet(Route, async (CancellationToken cancellationToken) =>
            Results.Json(await _ReadAsync(services, cancellationToken).ConfigureAwait(false), ConnectKeyEndpoints.Json))
            .RequireAdmin().Audited("assistant_settings", services);

        api.MapPut(Route + "/enabled", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var (body, refused) = await ProfileEndpoints.ReadBodyAsync<AssistantEnabledBody>(request, "The body names only enabled.", cancellationToken).ConfigureAwait(false);
            if (body is not { Enabled: { } enabled })
            {
                return await _AuditAsync(services, "api:assistant_enabled", "refused", null, refused).ConfigureAwait(false);
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await settings().LoadAsync(cancellationToken).ConfigureAwait(false);
                await settings().SaveAsync(stored with { IsEnabled = enabled }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }

            await host().ApplySettingsAsync(CancellationToken.None).ConfigureAwait(false);
            return await _AnswerAsync(services).ConfigureAwait(false);
        }).RequireAdmin();

        api.MapPatch(Route + "/profile", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var (patch, refused) = await ProfileEndpoints.ReadBodyAsync<RemoteAssistantProfilePatch>(
                request, "A change to the assistant's profile names only label, instructions, replacesStandingInstruction and profile.", cancellationToken).ConfigureAwait(false);
            if (patch is null)
            {
                return await _AuditAsync(services, "api:assistant_profile", "refused", null, refused).ConfigureAwait(false);
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await slot().LoadAsync(cancellationToken).ConfigureAwait(false);
                var (changed, refusal) = _Apply(current, patch, services);
                if (changed is null)
                {
                    return await _AuditAsync(services, "api:assistant_profile", "refused", null, refusal).ConfigureAwait(false);
                }

                await slot().RepointAsync(changed, patch.ReplacesStandingInstruction ?? current.ReplacesStandingInstruction, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }

            return await _AuditAsync(services, "api:assistant_profile", "assistant profile changed", null, await _RestartAsync(services, host()).ConfigureAwait(false)).ConfigureAwait(false);
        }).RequireAdmin();

        // The server copies its own record, secrets and all, so they never cross; the audit names the label it stored.
        api.MapPost(Route + "/profile/copy-from/{label}", async (string label, CancellationToken cancellationToken) =>
        {
            SessionProfile source;
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var profiles = await services.GetRequiredService<ISessionProfileStore>().LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = ProfileEndpoints.IndexOf(profiles, label);
                if (index < 0)
                {
                    return await _AuditAsync(services, "api:assistant_copy_profile", "refused", null, ProfileEndpoints.NoProfile()).ConfigureAwait(false);
                }

                source = profiles[index];
                var current = await slot().LoadAsync(cancellationToken).ConfigureAwait(false);
                await slot().RepointAsync(source, current.ReplacesStandingInstruction, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }

            return await _AuditAsync(services, "api:assistant_copy_profile", "copied", source.Label, await _RestartAsync(services, host()).ConfigureAwait(false)).ConfigureAwait(false);
        }).RequireAdmin();
    }

    // A profile change restarts the assistant, as the desktop's "Save and restart the assistant" does. Not tied to the
    // request: a caller that goes away must not leave the assistant half restarted.
    private static async Task<IResult> _RestartAsync(IServiceProvider services, IAssistantSessionHost host)
    {
        await host.ApplySettingsAsync(CancellationToken.None).ConfigureAwait(false);
        await host.RestartAsync(CancellationToken.None).ConfigureAwait(false);
        return await _AnswerAsync(services).ConfigureAwait(false);
    }

    private static async Task<IResult> _AnswerAsync(IServiceProvider services) =>
        Results.Json(await _ReadAsync(services, CancellationToken.None).ConfigureAwait(false), ConnectKeyEndpoints.Json);

    private static async Task<RemoteAssistantSettings> _ReadAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = await services.GetRequiredService<IAssistantSettingsStore>().LoadAsync(cancellationToken).ConfigureAwait(false);
        var slot = await services.GetRequiredService<IAssistantProfileStore>().LoadAsync(cancellationToken).ConfigureAwait(false);
        var host = services.GetRequiredService<IAssistantSessionHost>();
        var registry = services.GetService<IPluginProviderRegistry>();
        var profile = slot.Profile;

        // The server never applies its settings at start, so its host reads "ready" until something has: off or empty is not.
        return new RemoteAssistantSettings(
            settings.IsEnabled,
            settings.IsEnabled && profile is not null && host.Activity != AssistantActivity.Unavailable,
            host.UnavailableReason,
            profile is null ? null : ProfileEndpoints.ToWire(profile, ProfileEndpoints.Health(services), ProfileEndpoints.Declared(services, profile)),
            profile is null ? slot.UnsetReason : null,
            profile?.SystemPrompt,
            slot.ReplacesStandingInstruction,
            profile?.ProviderConfig is not PluginProviderConfig plugin || registry is null || registry.Resolve(plugin.ProviderId) is not null,
            new RemoteConsentBypass(settings.ConsentBypassAll, [.. settings.ConsentBypassSources.Concat(settings.ConsentBypassDangerousSources).Distinct(StringComparer.Ordinal)]));
    }

    // AC-1473's per-field rule on the slot's record, plus the slot's own label and instructions.
    private static (SessionProfile? Profile, IResult Refusal) _Apply(AssistantProfileSlot slot, RemoteAssistantProfilePatch patch, IServiceProvider services)
    {
        if (slot.Profile is not { } profile)
        {
            return (null, BackendApiRoutes.Error(StatusCodes.Status409Conflict, "no_assistant_profile", "No Assistant Profile is set. Copy one in from the server's profiles first."));
        }

        if (patch.Label is { } label && string.IsNullOrWhiteSpace(label))
        {
            return (null, BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The assistant's profile needs a label."));
        }

        var changed = profile;
        if (patch.Profile is { } fields)
        {
            var (applied, refusal) = ProfileEndpoints.Apply(profile, fields, ProfileEndpoints.Declared(services, profile));
            if (applied is null)
            {
                return (null, refusal);
            }

            changed = applied;
        }

        return (changed with
        {
            Label = patch.Label?.Trim() ?? changed.Label,
            SystemPrompt = patch.Instructions is { } instructions ? (string.IsNullOrWhiteSpace(instructions) ? null : instructions) : changed.SystemPrompt,
        }, Results.Empty);
    }

    // Every outcome into the node access audit with the calling key as the actor, as `Audited` writes it, but in the
    // words of what changed. `subject` is only ever what the server stored, never what the request carried.
    private static async Task<IResult> _AuditAsync(IServiceProvider services, string action, string outcome, string? subject, IResult answer)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit && McpRequestContext.CurrentNodeCaller is { } caller)
        {
            await audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, action, outcome, subject), CancellationToken.None).ConfigureAwait(false);
        }

        return answer;
    }
}

internal sealed record AssistantEnabledBody(bool? Enabled);
