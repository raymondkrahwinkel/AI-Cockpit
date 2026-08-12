using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-747: <c>WriteAsync</c>/<c>RestoreAsync</c> used to run their synchronous archiving/unpacking loop on whichever
/// thread called them, freezing the UI for the length of a backup; both now offload to <c>Task.Run</c>. The state
/// root (<c>CockpitConfigPath.Root</c>) is a fixed, non-test-overridable OS path — a real dev machine's is
/// multi-gigabyte — so instead of archiving it for real, this proves the offload is wired up by showing an
/// already-cancelled token short-circuits <c>Task.Run</c> before the method body (and the state root) is touched.
/// </summary>
public class BackupServiceOffloadsToTheThreadPoolTests
{
    private static BackupService _Service() => new(new _NoProfiles(), NullLogger<BackupService>.Instance);

    [Fact]
    public async Task WriteAsync_WithAnAlreadyCancelledToken_NeverRunsItsBody()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _Service().WriteAsync("unused.zip", new BackupOptions(), cancelled.Token));
    }

    [Fact]
    public async Task RestoreAsync_WithAnAlreadyCancelledToken_NeverRunsItsBody()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _Service().RestoreAsync("unused.zip", new RestoreOptions(false, []), cancelled.Token));
    }

    // Never reached: an already-cancelled Task.Run must not invoke the delegate, so this stands in as a tripwire —
    // if either test above ever calls into it, that is this fix having regressed.
    private sealed class _NoProfiles : ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not reached if the offload still short-circuits on a cancelled token.");

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not reached if the offload still short-circuits on a cancelled token.");
    }
}
