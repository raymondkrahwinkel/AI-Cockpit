using System.Diagnostics;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// A desktop notification on Linux (#76), through <c>notify-send</c> — the one interface every desktop here actually
/// implements, and the one that does not put a D-Bus dependency in an app that has no other use for one.
/// <para>
/// Until now this platform got <see cref="NoOpToastNotifier"/>, which means the "you are at the machine" half of the
/// notification router delivered nothing at all on the machine this cockpit is mostly used from. Only the away half
/// (Discord) ever arrived.
/// </para>
/// <para>
/// A notification that cannot be delivered is logged and dropped. A cockpit that dies because a desktop has no
/// notification daemon would be a worse failure than the missed toast — and the operator is, by definition, sitting
/// right there and will see the session anyway.
/// </para>
/// </summary>
internal sealed class LinuxToastNotifier(ILogger<LinuxToastNotifier> logger) : IToastNotifier
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public async Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "notify-send",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--app-name=Cockpit");

        // Normal, not critical: critical notifications on most desktops never expire, and a cockpit that leaves a
        // stack of undismissable banners behind is one you turn notifications off for.
        startInfo.ArgumentList.Add("--urgency=normal");
        startInfo.ArgumentList.Add(notification.Title);
        startInfo.ArgumentList.Add(notification.Body);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogDebug("notify-send did not start; the notification was dropped.");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);

            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                logger.LogDebug("notify-send exited with {Code}: {Error}", process.ExitCode, error.Trim());
            }
        }
        catch (Exception exception)
        {
            // No notify-send installed, no notification daemon running, a desktop that refuses: none of it is worth
            // taking the cockpit down for.
            logger.LogDebug(exception, "A desktop notification could not be delivered.");
        }
    }
}
