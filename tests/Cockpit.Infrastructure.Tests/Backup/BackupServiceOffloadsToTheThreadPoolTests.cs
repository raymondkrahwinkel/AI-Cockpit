using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-747: <c>WriteAsync</c>/<c>RestoreAsync</c> used to run their whole synchronous archiving/unpacking loop on
/// whichever thread called them — the UI thread, in practice — freezing the app for the length of a backup. Both
/// now hand that work to the thread pool via <c>Task.Run</c>.
/// <para>
/// The state root is a fixed OS path (<c>CockpitConfigPath.Root</c>), not test-overridable, so a test that exercises
/// the real archiving/unpacking loop against a realistic file count is not possible here without archiving the
/// developer's real, multi-gigabyte Cockpit state directory. What this proves instead: an already-cancelled token
/// must short-circuit <c>Task.Run</c> before the method body runs at all — the exact mechanism the fix relies on —
/// which the pre-fix, fully-synchronous body could not do (its own cancellation check only ran after already
/// touching the state root, one file into the loop).
/// </para>
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
