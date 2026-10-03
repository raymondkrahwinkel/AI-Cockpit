using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1470: the server's health behind an operate key: sign-in per profile, the server's own facts and the plugin
// sections' rows within the key's scope, and the actions those rows offer. /healthz stays the anonymous summary.
// Written out field by field: no token, credential or key prefix crosses.
internal static partial class HealthEndpoints
{
    public const int MaxLabelLength = 120;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/health", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var pairing = services.GetRequiredService<INodePairingBroker>();
            var sections = await services.GetRequiredService<PluginHealthSections>().ReadAsync().ConfigureAwait(false);
            var keys = await services.GetRequiredService<ConnectKeyVerifier>().ListAsync(cancellationToken).ConfigureAwait(false);
            var assistant = services.GetService<NodeControllerPresence>()?.Current;
            var now = DateTimeOffset.UtcNow;

            // A key with a narrowed scope does not learn which other keys exist; it sees only itself.
            var seesAllKeys = (caller.Scope ?? ConnectKeyScope.Default) is { AllowAllProfiles: true, AllowAllProjects: true };
            await _AuditAsync(services, caller, "api:health", "called", null, cancellationToken).ConfigureAwait(false);

            return Results.Json(new
            {
                profiles = (services.GetService<IProfileLoginHealth>()?.Current ?? [])
                    .Where(profile => caller.AllowsProfile(profile.Profile, pairing))
                    .Select(profile => new
                    {
                        label = profile.Profile,
                        provider = profile.Provider,
                        signIn = _SignIn(profile.SignIn),
                        lastCheck = profile.LastCheck,
                        expiredSince = profile.ExpiredSince,
                        announcedAt = profile.AnnouncedAt,
                    }),
                server = new
                {
                    version = BackendApiRoutes.HostVersion(),
                    image = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? Clean(image) : null,
                    startedAt = BackendApiRoutes.StartedAt,
                    address = _Address(),
                    assistant = assistant is null ? null : new { holder = assistant.Name, since = assistant.SinceUtc },
                    keys = keys.Where(entry => entry.Key.IsUsableAt(now) && (seesAllKeys || entry.Key.Prefix == caller.KeyPrefix)).Select(entry => new
                    {
                        label = entry.Key.Label,
                        capability = BackendApiRoutes.CapabilityName(entry.Key.Capability),
                        lastUsedAt = entry.LastUsedAt,
                    }),
                },
                sections = sections.Select(section => new
                {
                    name = section.Name,
                    healthy = section.Report.Healthy,
                    rows = _RowsInScope(section.Report, caller, pairing).Select(row => new
                    {
                        label = Clean(row.Label),
                        status = row.Status == PluginHealthStatus.Ok ? "ok" : "failed",
                        at = row.At,
                        projectId = row.ProjectId is null ? null : Clean(row.ProjectId),
                        actionId = section.Actions is not null && _IsActionId(row.ActionId) ? row.ActionId : null,
                    }),
                }),
            });
        }).RequireOperate();

        // A row outside the key's scope, an unknown section or action, and a section without actions all get one
        // answer: the 404 of "does not exist". An id that any out-of-scope row also offers is out of scope as a whole.
        api.MapPost("/health/{section}/actions/{actionId}", async (string section, string actionId, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var pairing = services.GetRequiredService<INodePairingBroker>();
            var reading = (await services.GetRequiredService<PluginHealthSections>().ReadAsync().ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.Name, section, StringComparison.Ordinal));
            var offering = (reading.Report?.Rows ?? []).Where(row => row is not null && string.Equals(row.ActionId, actionId, StringComparison.Ordinal)).ToList();
            var inScope = _RowsInScope(reading.Report, caller, pairing).Count(row => string.Equals(row.ActionId, actionId, StringComparison.Ordinal));
            if (reading.Actions is not { } actions || !_IsActionId(actionId) || offering.Count == 0 || inScope != offering.Count)
            {
                await _AuditAsync(services, caller, "api:health_action", "not found", Clean($"{section}/{actionId}"), CancellationToken.None).ConfigureAwait(false);
                return Results.NotFound();
            }

            var succeeded = false;
            try
            {
                succeeded = (await actions.RunAsync(actionId, cancellationToken).ConfigureAwait(false))?.Succeeded == true;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HealthEndpoints)).LogWarning(exception, "Health action {Action} of section {HealthSection} failed.", actionId, reading.Name);
            }
            catch (OperationCanceledException)
            {
                await _AuditAsync(services, caller, "api:health_action", "cancelled", $"{reading.Name}/{actionId}", CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await _AuditAsync(services, caller, "api:health_action", succeeded ? "succeeded" : "failed", $"{reading.Name}/{actionId}", CancellationToken.None).ConfigureAwait(false);
            return Results.Json(new { section = reading.Name, actionId, succeeded });
        }).RequireOperate();
    }

    // AC-1470: set by deploy/compose.yaml from the image the container runs; absent elsewhere.
    public const string ImageVariable = "COCKPIT_IMAGE_REF";

    // A plugin's label, made safe to show elsewhere: no control, format or line/paragraph separator characters (line
    // breaks, bidi overrides) and at most MaxLabelLength characters, never split inside a surrogate pair.
    public static string Clean(string? text)
    {
        var kept = new StringBuilder();
        foreach (var character in text ?? "")
        {
            if (kept.Length == MaxLabelLength)
            {
                break;
            }

            if (!char.IsControl(character)
                && CharUnicodeInfo.GetUnicodeCategory(character) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            {
                kept.Append(character);
            }
        }

        if (kept.Length > 0 && char.IsHighSurrogate(kept[^1]))
        {
            kept.Length--;
        }

        return kept.ToString();
    }

    // Rows without a project reach every key that may read the health; a row with one only a key scoped to it.
    private static IEnumerable<PluginHealthRow> _RowsInScope(PluginHealthReport? report, NodeCaller caller, INodePairingBroker pairing) =>
        (report?.Rows ?? []).Where(row => row is not null && (row.ProjectId is null || caller.AllowsProject(row.ProjectId, pairing)));

    private static bool _IsActionId(string? actionId) => actionId is not null && _ActionId().IsMatch(actionId);

    // Inside a container the helper picks the bridge address, which no client can reach: no address beats a wrong one.
    private static string? _Address() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? null
            : NodeReachableAddress.Resolve();

    private static string _SignIn(ProfileSignInKind kind) => kind switch
    {
        ProfileSignInKind.SignedIn => "signedIn",
        ProfileSignInKind.Expired => "expired",
        _ => "unchecked",
    };

    private static async Task _AuditAsync(IServiceProvider services, NodeCaller caller, string tool, string outcome, string? subject, CancellationToken cancellationToken)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit)
        {
            await audit.RecordAsync(new NodeAccessAuditEntry(DateTimeOffset.UtcNow, caller.Credential, caller.KeyPrefix, caller.RemoteAddress, tool, outcome, subject), cancellationToken).ConfigureAwait(false);
        }
    }

    // Only reached behind the group's door, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A health route ran without a connect-key caller.");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z")]
    private static partial Regex _ActionId();
}
