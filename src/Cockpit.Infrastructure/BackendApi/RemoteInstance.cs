using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
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
        Path.Combine(Path.TrimEndingDirectorySeparator(localRoot) + "-remote", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server))));
}

// AC-1487: the local root's registry as a `--remote` window knows it: read once into memory, never written or repaired.
// Encrypted credentials stay ciphertext until `Protection` is unlocked, and that key lives in this object only.
public sealed class LocalRegistry
{
    private static readonly JsonSerializerOptions SecurityOptions = new();

    private readonly JsonNode? _document;
    private readonly SecretKeyHolder _keys = new();

    private LocalRegistry(JsonNode? document)
    {
        _document = document;
        Protection = new ReadOnlyUnlock(this);
    }

    // What the desktop's unlock window asks. Only the unlock works; every change belongs to the local Cockpit.
    public ISecretProtectionService Protection { get; }

    public bool IsLocked => _Security() is { Enabled: true } && _keys.Protector is null;

    public static async Task<LocalRegistry> ReadAsync(string localRoot, CancellationToken cancellationToken = default)
    {
        try
        {
            return new LocalRegistry(await CockpitConfigFileAccess.ReadOnceAsync(CockpitConfigPath.For(localRoot), cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local Cockpit's configuration does not read. Open the local Cockpit first; it repairs it.", exception);
        }
    }

    // The one registry row for `server`, connected; null when the registry holds no connect key by that name.
    public RemoteInstanceConnection? Connect(string server, ILoggerFactory loggers)
    {
        var rows = _document is null ? null : CockpitConfigFileAccess.Decode(_document, _keys)?.McpServers;
        if (rows?.Select(entry => entry.ToDomain()).FirstOrDefault(row => RemoteServers.ServerNameOf(row) == server) is not { } row)
        {
            return null;
        }

        var registry = new OneRow(row);
        return new RemoteInstanceConnection(
            new RemoteServers(registry, loggers.CreateLogger<RemoteServers>()),
            new NodeSessionsClient(registry, new NoDiscovery(), loggers.CreateLogger<NodeSessionsClient>()));
    }

    private SecretProtectionEntry? _Security() => _document?["Security"]?.Deserialize<SecretProtectionEntry>(SecurityOptions);

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

    // The password checked against the snapshot's verifier, as the desktop checks it, but without its sidecar scrub.
    private sealed class ReadOnlyUnlock(LocalRegistry registry) : ISecretProtectionService
    {
        private const string LocalOnly = "Change the encryption from the local Cockpit; a remote window writes nothing there.";

        public Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretProtectionStatus(registry._Security() is { Enabled: true }, registry._keys.Protector is not null));

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default)
        {
            if (registry._Security() is not { Enabled: true } security)
            {
                return Task.FromResult(true);
            }

            var protector = new SecretProtector(SecretKey.Derive(password, Convert.FromBase64String(security.Salt), security.Iterations, security.Kdf));
            if (!SecretProtectionService.VerifierMatches(protector, security))
            {
                return Task.FromResult(false);
            }

            registry._keys.Unlock(protector);
            return Task.FromResult(true);
        }

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException(LocalOnly);

        public Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(LocalOnly);

        public Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(LocalOnly);

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(LocalOnly);

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException(LocalOnly);
    }
}

// What the remote window works over: the one server, and its node tools for the start card and Admin's choices.
public sealed record RemoteInstanceConnection(IRemoteServers Servers, INodeSessionsClient Nodes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Servers is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
