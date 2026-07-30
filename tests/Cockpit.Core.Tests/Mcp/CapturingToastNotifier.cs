using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Collects what the operator would have been shown, so "they were told" is something a test can assert rather than
/// something the log has to be read for.
/// </summary>
internal sealed class CapturingToastNotifier : IToastNotifier
{
    private readonly ConcurrentQueue<AttentionNotification> _shown = new();

    public IReadOnlyCollection<AttentionNotification> Shown => [.. _shown];

    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        _shown.Enqueue(notification);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> notifications have arrived, or gives up. The coordinator shows
    /// them fire-and-forget — it must not make a credential wait on a desktop — so an assertion the instant after
    /// the call would be racing the notification rather than checking it.
    /// </summary>
    public async Task<bool> WaitForAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_shown.Count >= count)
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return _shown.Count >= count;
    }
}
