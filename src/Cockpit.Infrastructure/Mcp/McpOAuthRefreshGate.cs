using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-1586: one refresh-token redemption per server at a time, whoever asks. The server rotates the refresh token on
// every redemption, so two at once replay a spent one and can get the whole grant revoked.
internal sealed class McpOAuthRefreshGate : ISingletonService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string serverId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
