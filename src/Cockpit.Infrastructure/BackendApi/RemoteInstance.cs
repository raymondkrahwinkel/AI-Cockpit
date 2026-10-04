using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1487: `Cockpit --remote <server>`, a window on one connect server with a state root of its own and no backend.
// The connect key is read once from the operator's own registry; nothing is ever written back to it.
public static class RemoteInstance
{
    public const string Argument = "--remote";

    // The server named after `--remote`, or null when the command line asks for no remote window.
    public static string? ServerNameFrom(string[] args) =>
        Array.IndexOf(args, Argument) is var at and >= 0 && at + 1 < args.Length && !string.IsNullOrWhiteSpace(args[at + 1])
            ? args[at + 1]
            : null;

    // Beside the local root rather than inside it, so that root stays byte-equal. Hashed: a node name is unconstrained.
    public static string StateRootFor(string localRoot, string server) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(localRoot) + "-remote", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server)))[..16]);

    // The one registry row for `server` in `localRoot`, connected; null when that root holds no connect key by that name.
    public static async Task<RemoteInstanceConnection?> ConnectAsync(string localRoot, string server, ILoggerFactory loggers, CancellationToken cancellationToken = default)
    {
        var rows = await new McpServerStore(Configuration.CockpitConfigPath.For(localRoot)).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (rows.FirstOrDefault(row => RemoteServers.ServerNameOf(row) == server) is not { } row)
        {
            return null;
        }

        var registry = new OneRow(row);
        return new RemoteInstanceConnection(
            new RemoteServers(registry, loggers.CreateLogger<RemoteServers>()),
            new NodeSessionsClient(registry, new NoDiscovery(), loggers.CreateLogger<NodeSessionsClient>()));
    }

    // The registry as this window knows it: the one row, held in memory. A node that moved is remembered for this run only.
    private sealed class OneRow(McpServerConfig row) : IMcpServerStore
    {
        private IReadOnlyList<McpServerConfig> _rows = [row];

        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_rows);

        public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default)
        {
            _rows = servers;
            return Task.CompletedTask;
        }
    }

    // No multicast query: listening for the answers would open a port, and this window opens none.
    private sealed class NoDiscovery : INodeDiscoveryClient
    {
        public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NodeDiscoveryFound>>([]);
    }
}

// What the remote window works over: the one server, and its node tools for the start card and Admin's choices.
public sealed record RemoteInstanceConnection(IRemoteServers Servers, INodeSessionsClient Nodes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Servers is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
