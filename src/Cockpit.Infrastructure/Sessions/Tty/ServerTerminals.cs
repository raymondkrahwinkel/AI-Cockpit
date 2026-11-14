using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-2288: the plain shells remote windows opened on this server, each owned by the connect key that opened it.
// A shell without a reader for `DetachedGrace` is closed, and so is every shell of a key the moment it is revoked.
internal sealed class ServerTerminals(ILogger<ServerTerminals> logger, TimeProvider? time = null) : IAsyncDisposable
{
    public const int MaxPerKey = 8;
    public const int MaxTotal = 32;
    public static readonly TimeSpan DetachedGrace = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Shell> _shells = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public sealed record Shell(string Id, string OwnerPrefix, string Name, ServerPty Pty);

    public Shell? Find(string id) => _shells.GetValueOrDefault(id);

    // Null when the key or the server already holds as many shells as it may.
    public Shell? Add(string ownerPrefix, string name, ServerPty pty, CancellationToken revoked)
    {
        lock (_shells)
        {
            if (_shells.Count >= MaxTotal || _shells.Values.Count(shell => shell.OwnerPrefix == ownerPrefix) >= MaxPerKey)
            {
                return null;
            }

            var shell = new Shell(Guid.NewGuid().ToString("n"), ownerPrefix, name, pty);
            _shells[shell.Id] = shell;
            pty.Exited += () => _ = CloseAsync(shell.Id);
            revoked.Register(() => _ = CloseAsync(shell.Id));
            logger.LogInformation("Opened server terminal {Terminal} for connect key {Prefix}.", shell.Id, ownerPrefix);
            return shell;
        }
    }

    // Called when a reader lets go: closes the shell unless a reader came back within the grace.
    public void Detached(string id)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(DetachedGrace, _time).ConfigureAwait(false);
            if (Find(id) is { Pty.HasReader: false })
            {
                await CloseAsync(id).ConfigureAwait(false);
            }
        });
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!_shells.TryRemove(id, out var shell))
        {
            return false;
        }

        await shell.Pty.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("Closed server terminal {Terminal}.", id);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _shells.Keys.ToList())
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }
}
