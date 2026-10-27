using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// A one-shot app-server read: it has no thread and therefore does not consume model tokens.
internal static class CodexModelIntelligence
{
    private const string ClientName = "cockpit";
    private const string ClientVersion = "1.0.0";
    private const string ProviderId = "cli-agent-provider.codex";
    private const string SourceName = "codex app-server account/rateLimits/read";

    // Default TTL (AC-1855): zero made every reading stale the moment it was read; the age stays visible regardless.
    private static readonly TimeSpan QuotaValidFor = TimeSpan.FromMinutes(5);

    public static async Task<PluginModelIntelligence> ReadAsync(
        Func<ICliSubprocess> subprocessFactory,
        CliAgentConfig config,
        string executablePath,
        Action<PluginModelObservation>? recordObservation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new CodexAppServerConnection(subprocessFactory());
            connection.Start(executablePath, _WorkingDirectory(config), config.BuildEnvironmentVariables());

            await connection.SendRequestAsync("initialize", new { clientInfo = new { name = ClientName, version = ClientVersion } }, cancellationToken).ConfigureAwait(false);
            await connection.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
            var listing = CodexModelCatalog.ParseListing(await connection.SendRequestAsync("model/list", new { }, cancellationToken).ConfigureAwait(false));
            var asOf = DateTimeOffset.UtcNow;
            var source = new PluginFactSource(SourceName, asOf, QuotaValidFor);
            PluginQuotaState[] quota;
            try
            {
                var rateLimits = await connection.SendRequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
                quota = [.. CodexRateLimits.Parse(rateLimits).Select(window => new PluginQuotaState(window, source))];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // AC-1855: logged out means quota unknown; the listed models (and so their public price) still stand.
                quota = [];
            }

            foreach (var state in quota)
            {
                recordObservation?.Invoke(new PluginModelObservation(ProviderId, PluginModelObservationKind.QuotaReading, asOf)
                {
                    Quota = state.Window,
                });
            }

            return new PluginModelIntelligence
            {
                // Codex reports these ids live; no price, modifier, or alias is invented here.
                Models = listing.Ids.Select(id => new PluginModelCostEstimate(id) { PublicModelRef = id }).ToArray(),
                Quota = quota,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A missing CLI, expired login, or an older app-server is unknown, never a failed host read.
            return new PluginModelIntelligence();
        }
    }

    private static string _WorkingDirectory(CliAgentConfig config) =>
        string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Environment.CurrentDirectory : config.WorkingDirectory;
}
