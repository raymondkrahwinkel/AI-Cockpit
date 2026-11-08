using Cockpit.Core.Plugins;
using System.Runtime.CompilerServices;

namespace Cockpit.App.Services;

internal static class PluginStoreFetching
{
    public static async IAsyncEnumerable<(PluginStoreConfig Store, PluginStoreFetchResult Fetch)> FetchAllAsync(
        IReadOnlyList<PluginStoreConfig> stores,
        Func<PluginStoreConfig, CancellationToken, Task<PluginStoreFetchResult>> fetch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!stores.Any(store => store.Signed is not null))
        {
            foreach (var store in stores)
            {
                yield return (store, await FetchAsync(store, token => fetch(store, token), cancellationToken).ConfigureAwait(false));
            }
            yield break;
        }

        var pending = stores.Select(async store =>
            (Store: store, Fetch: await FetchAsync(store, token => fetch(store, token), cancellationToken).ConfigureAwait(false)));
        await foreach (var completed in Task.WhenEach(pending).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return await completed.ConfigureAwait(false);
        }
    }

    public static async Task<PluginStoreFetchResult> FetchAsync(
        PluginStoreConfig store,
        Func<CancellationToken, Task<PluginStoreFetchResult>> fetch,
        CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (store.Signed is not null)
        {
            request.CancelAfter(TimeSpan.FromSeconds(10));
        }
        try
        {
            return await fetch(request.Token).WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new PluginStoreFetchResult(false, $"Could not fetch the store index: {exception.Message}", null, null);
        }
    }
}
