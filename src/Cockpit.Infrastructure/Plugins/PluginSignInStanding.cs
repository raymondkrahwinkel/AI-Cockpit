using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// AC-1663: whether a plugin is waiting for the operator to sign in. The host already says that once, with a Sign in
// button (AC-1517), so a background job of that plugin that fails for the same reason stays quiet instead of adding a toast.
public sealed class PluginSignInStanding(ILogger<PluginSignInStanding> logger) : ISingletonService
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<bool>>> _plugins = new(StringComparer.Ordinal);

    internal void Register(string pluginId, Func<CancellationToken, Task<bool>> waitsForSignIn) => _plugins[pluginId] = waitsForSignIn;

    // False for no plugin, an unknown one, or a question that fails: a real error is never hidden behind a guess.
    public async Task<bool> WaitsAsync(string? pluginId, CancellationToken cancellationToken = default)
    {
        if (pluginId is null || !_plugins.TryGetValue(pluginId, out var waits))
        {
            return false;
        }

        try
        {
            return await waits(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not tell whether plugin {PluginId} waits for a sign-in; treating it as not.", pluginId);
            return false;
        }
    }
}
